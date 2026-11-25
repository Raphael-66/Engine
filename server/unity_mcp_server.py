#!/usr/bin/env python3
"""
Servidor MCP (stdio) para o Unity Editor.

Repassa as chamadas de ferramentas do Claude para a bridge HTTP que roda dentro
do Editor (pacote com.claude.unity-mcp), em http://127.0.0.1:8090 por padrão.

Sem dependências: só a biblioteca padrão do Python 3.8+.

Variáveis de ambiente:
  UNITY_MCP_HOST   (padrão 127.0.0.1)
  UNITY_MCP_PORT   (padrão 8090)
  UNITY_MCP_TIMEOUT segundos por chamada (padrão 70)
"""
import json
import os
import sys
import urllib.error
import urllib.request

SERVER_NAME = "unity-engine"
SERVER_VERSION = "1.0.0"
HOST = os.environ.get("UNITY_MCP_HOST", "127.0.0.1")
PORT = int(os.environ.get("UNITY_MCP_PORT", "8090"))
TIMEOUT = float(os.environ.get("UNITY_MCP_TIMEOUT", "70"))
BASE = f"http://{HOST}:{PORT}"

# ---------------------------------------------------------------- schemas
VEC3 = {
    "type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3,
    "description": "[x, y, z]",
}
TARGET = {
    "type": "string",
    "description": "GameObject: instanceId, caminho na hierarquia ('Pai/Filho') ou nome único.",
}


def tool(name, description, props=None, required=None, read_only=False, destructive=False):
    t = {
        "name": name,
        "description": description,
        "inputSchema": {"type": "object", "properties": props or {}, "required": required or []},
        "annotations": {"readOnlyHint": read_only, "destructiveHint": destructive},
    }
    return t


TOOLS = [
    tool("get_project_info", "Versão do Unity, nome do projeto, plataforma, render pipeline, cenas abertas e estado do Play Mode.",
         read_only=True),
    tool("get_hierarchy", "Árvore de GameObjects das cenas abertas (ou a partir de um objeto raiz).", {
        "root": {**TARGET, "description": "Opcional: começa a árvore neste objeto."},
        "max_depth": {"type": "integer", "default": 10},
        "include_components": {"type": "boolean", "default": True},
    }, read_only=True),
    tool("find_gameobjects", "Busca GameObjects por parte do nome, tag e/ou tipo de componente (inclui inativos).", {
        "name": {"type": "string"}, "tag": {"type": "string"},
        "component": {"type": "string", "description": "Nome do tipo, ex.: 'Rigidbody', 'PlayerController'."},
        "limit": {"type": "integer", "default": 100},
    }, read_only=True),
    tool("get_gameobject", "Detalhes de um GameObject: transform, tag, layer, filhos, componentes e suas propriedades serializadas.", {
        "target": TARGET, "include_properties": {"type": "boolean", "default": True},
    }, ["target"], read_only=True),
    tool("create_gameobject", "Cria um GameObject vazio ou primitivo, opcionalmente com pai, transform, componentes e tag.", {
        "name": {"type": "string"},
        "primitive": {"type": "string", "enum": ["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"]},
        "parent": TARGET, "position": VEC3, "rotation": {**VEC3, "description": "Ângulos de Euler em graus"},
        "scale": VEC3, "local": {"type": "boolean", "default": False, "description": "Usar coordenadas locais"},
        "components": {"type": "array", "items": {"type": "string"}}, "tag": {"type": "string"},
    }, ["name"]),
    tool("delete_gameobject", "Apaga um GameObject da cena (pode ser desfeito com Ctrl+Z no Editor).", {"target": TARGET},
         ["target"], destructive=True),
    tool("duplicate_gameobject", "Duplica um GameObject.", {
        "target": TARGET, "new_name": {"type": "string"}, "position": VEC3, "rotation": VEC3, "scale": VEC3,
    }, ["target"]),
    tool("set_transform", "Altera posição, rotação (Euler) e/ou escala de um GameObject.", {
        "target": TARGET, "position": VEC3, "rotation": VEC3, "scale": VEC3,
        "local": {"type": "boolean", "default": False},
    }, ["target"]),
    tool("set_parent", "Muda o pai de um GameObject (parent vazio = raiz da cena).", {
        "target": TARGET, "parent": TARGET, "keep_world_position": {"type": "boolean", "default": True},
    }, ["target"]),
    tool("set_active", "Ativa ou desativa um GameObject.", {"target": TARGET, "active": {"type": "boolean"}},
         ["target", "active"]),
    tool("add_component", "Adiciona um componente (ex.: Rigidbody, BoxCollider, AudioSource ou um script seu).", {
        "target": TARGET, "component": {"type": "string"},
    }, ["target", "component"]),
    tool("remove_component", "Remove um componente de um GameObject.", {
        "target": TARGET, "component": {"type": "string"},
    }, ["target", "component"], destructive=True),
    tool("set_property",
         "Define uma propriedade de um componente, GameObject ou asset. Aceita o nome serializado (ex.: 'm_Mass', "
         "'speed') ou a propriedade C# pública (ex.: 'mass', 'intensity'). Vetores como [x,y,z], cores como "
         "[r,g,b,a] ou '#RRGGBB', enums pelo nome, referências como caminho de asset ('Assets/...') ou alvo na cena.", {
             "target": {"type": "string", "description": "GameObject na cena ou caminho de asset 'Assets/...'"},
             "component": {"type": "string", "description": "Tipo do componente; omita para o próprio GameObject/asset."},
             "property": {"type": "string"},
             "value": {"description": "Novo valor (número, texto, booleano, lista ou objeto)."},
         }, ["target", "property", "value"]),
    tool("list_assets", "Lista assets usando a sintaxe de busca do Project (ex.: 't:Prefab', 't:Material player', 't:Scene').", {
        "filter": {"type": "string", "default": ""}, "folder": {"type": "string", "default": "Assets"},
        "limit": {"type": "integer", "default": 200},
    }, read_only=True),
    tool("read_text_asset", "Lê um arquivo de texto do projeto (scripts .cs, .json, .shader, .txt...).", {
        "path": {"type": "string", "description": "Ex.: Assets/Scripts/Player.cs"},
    }, ["path"], read_only=True),
    tool("write_text_asset",
         "Cria ou sobrescreve um arquivo de texto no projeto (ex.: um script C#) e importa no Unity. "
         "Depois de escrever .cs, espere a compilação (get_compile_status) e confira erros (get_console_logs).", {
             "path": {"type": "string"}, "content": {"type": "string"},
         }, ["path", "content"], destructive=True),
    tool("create_material", "Cria um material (.mat) com o shader padrão do pipeline atual e opcionalmente o aplica a um objeto.", {
        "path": {"type": "string", "description": "Ex.: Assets/Materials/Vermelho.mat"},
        "color": {"description": "[r,g,b,a] (0-1 ou 0-255) ou '#RRGGBB'"},
        "shader": {"type": "string"}, "metallic": {"type": "number"}, "smoothness": {"type": "number"},
        "assign_to": TARGET,
    }, ["path"]),
    tool("instantiate_prefab", "Coloca um prefab na cena.", {
        "path": {"type": "string"}, "name": {"type": "string"}, "parent": TARGET,
        "position": VEC3, "rotation": VEC3, "scale": VEC3,
    }, ["path"]),
    tool("create_prefab", "Salva um GameObject da cena como prefab.", {
        "target": TARGET, "path": {"type": "string", "description": "Ex.: Assets/Prefabs/Inimigo.prefab"},
    }, ["target", "path"]),
    tool("open_scene", "Abre uma cena (salva as abertas antes, por padrão).", {
        "path": {"type": "string"}, "additive": {"type": "boolean", "default": False},
        "save_current": {"type": "boolean", "default": True},
    }, ["path"]),
    tool("save_scene", "Salva a cena ativa (ou 'Salvar como' se path for informado).", {"path": {"type": "string"}}),
    tool("new_scene", "Cria uma cena nova (com câmera e luz, ou vazia) e opcionalmente salva no caminho dado.", {
        "path": {"type": "string"}, "empty": {"type": "boolean", "default": False},
        "save_current": {"type": "boolean", "default": True},
    }),
    tool("play_mode", "Controla o Play Mode.", {
        "action": {"type": "string", "enum": ["play", "stop", "pause", "step"]},
    }, ["action"]),
    tool("get_console_logs", "Últimas mensagens do Console do Unity.", {
        "count": {"type": "integer", "default": 50},
        "type": {"type": "string", "enum": ["Log", "Warning", "Error", "Exception", "Assert"]},
        "include_stack": {"type": "boolean", "default": False},
    }, read_only=True),
    tool("clear_console", "Limpa o Console do Unity."),
    tool("execute_menu_item", "Executa um item de menu do Editor, ex.: 'GameObject/Light/Directional Light' ou 'Edit/Undo'.", {
        "path": {"type": "string"},
    }, ["path"]),
    tool("select", "Seleciona e destaca um objeto da cena ou asset no Editor.", {
        "target": {"type": "string"}, "frame": {"type": "boolean", "default": True},
    }, ["target"]),
    tool("refresh_assets", "Reimporta assets alterados fora do Unity (AssetDatabase.Refresh)."),
    tool("get_compile_status", "Indica se o Unity está compilando e lista erros de compilação recentes.", read_only=True),
]
TOOL_NAMES = {t["name"] for t in TOOLS}


# ---------------------------------------------------------------- bridge
def call_unity(command, params):
    body = json.dumps({"command": command, "params": params or {}}).encode("utf-8")
    req = urllib.request.Request(f"{BASE}/command", data=body, method="POST",
                                 headers={"Content-Type": "application/json"})
    # Ignora proxies do sistema: a bridge é sempre local.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(req, timeout=TIMEOUT) as r:
            data = json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        try:
            data = json.loads(e.read().decode("utf-8"))
        except Exception:
            raise RuntimeError(f"Unity respondeu HTTP {e.code}")
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        raise RuntimeError(
            f"Não consegui falar com o Unity em {BASE} ({getattr(e, 'reason', e)}). "
            "Verifique se o Editor está aberto com o pacote com.claude.unity-mcp instalado "
            "(menu Tools > Claude MCP > Status). Se o Unity estiver recompilando scripts, espere alguns segundos."
        )
    if not data.get("ok"):
        raise RuntimeError(data.get("error") or "Erro desconhecido no Unity")
    return data.get("result")


# ---------------------------------------------------------------- MCP / JSON-RPC
def send(msg):
    sys.stdout.write(json.dumps(msg, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def log(*a):
    print("[unity-mcp]", *a, file=sys.stderr, flush=True)


def handle(msg):
    method = msg.get("method")
    mid = msg.get("id")
    params = msg.get("params") or {}

    if mid is None:  # notificação (ex.: notifications/initialized)
        return None

    if method == "initialize":
        client_ver = params.get("protocolVersion", "2025-06-18")
        return {"protocolVersion": client_ver,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
                "instructions": ("Controla o Unity Editor aberto. Comece com get_project_info e get_hierarchy. "
                                 "Use caminhos 'Assets/...' para assets. Após escrever scripts, aguarde a "
                                 "compilação e confira o console antes de adicionar o componente.")}
    if method == "ping":
        return {}
    if method == "tools/list":
        return {"tools": TOOLS}
    if method == "tools/call":
        name = params.get("name")
        args = params.get("arguments") or {}
        if name not in TOOL_NAMES:
            raise KeyError(f"Ferramenta desconhecida: {name}")
        try:
            result = call_unity(name, args)
            text = json.dumps(result, ensure_ascii=False, indent=2)
            return {"content": [{"type": "text", "text": text}], "isError": False}
        except Exception as e:
            return {"content": [{"type": "text", "text": f"Erro: {e}"}], "isError": True}
    if method in ("resources/list",):
        return {"resources": []}
    if method in ("prompts/list",):
        return {"prompts": []}
    raise NotImplementedError(method)


def main():
    log(f"iniciado; bridge Unity em {BASE}")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}})
            continue
        try:
            result = handle(msg)
            if result is not None:
                send({"jsonrpc": "2.0", "id": msg.get("id"), "result": result})
        except NotImplementedError as e:
            send({"jsonrpc": "2.0", "id": msg.get("id"), "error": {"code": -32601, "message": f"Método não suportado: {e}"}})
        except Exception as e:
            send({"jsonrpc": "2.0", "id": msg.get("id"), "error": {"code": -32603, "message": str(e)}})


if __name__ == "__main__":
    main()
