# Conector Unity Engine para o Claude (MCP)

Com ele o Claude controla o **Unity Editor aberto**: lê a hierarquia, cria e edita GameObjects, componentes, materiais, prefabs e cenas, escreve scripts C#, lê o Console e entra e sai do Play Mode.

```
Claude ──(MCP stdio)──► server/unity_mcp_server.py ──(HTTP 127.0.0.1:8090)──► Unity Editor (com.claude.unity-mcp)
```

> ⚠️ Versão 1.0: o servidor MCP foi testado, mas a parte em C# ainda não foi compilada num Unity real. Se der erro, abra uma issue com a mensagem do Console.

## 1. Instalar no Unity (2021.3 ou mais novo)

**Pelo GitHub:** Package Manager › **+ › Add package from git URL…** e cole
`https://github.com/Raphael-66/engine.git?path=/com.claude.unity-mcp`

**Ou manualmente:**
1. Copie a pasta `com.claude.unity-mcp` para a pasta `Packages/` do seu projeto Unity
   (ou use **Window › Package Manager › + › Add package from disk…** e escolha o `package.json`).
2. Quando o Unity terminar de compilar, o Console mostra:
   `[Claude MCP] Bridge ativa em http://127.0.0.1:8090/`
3. O menu **Tools › Claude MCP** inicia, para e mostra o status da bridge.

A bridge só aceita conexões de `localhost`.

## 2. Ligar ao Claude

É preciso ter Python 3.8+ instalado. O servidor não usa nenhuma dependência externa.

**Claude Desktop**: em *Settings › Developer › Edit Config*, abra o `claude_desktop_config.json` e adicione:

```json
{
  "mcpServers": {
    "unity": {
      "command": "python3",
      "args": ["/CAMINHO/ABSOLUTO/engine/server/unity_mcp_server.py"]
    }
  }
}
```
(No Windows, use `"command": "python"` e um caminho como `"C:\\Users\\voce\\engine\\server\\unity_mcp_server.py"`.) Depois, reinicie o Claude Desktop.

**Claude Code**:
```bash
claude mcp add unity -- python3 /CAMINHO/ABSOLUTO/engine/server/unity_mcp_server.py
```

Para trocar a porta, defina `UNITY_MCP_PORT` no servidor e a chave `ClaudeUnityMcp.Port` do EditorPrefs com o mesmo valor.

## 3. Ferramentas

| Área | Ferramentas |
|---|---|
| Projeto | `get_project_info`, `get_compile_status`, `refresh_assets` |
| Cena | `get_hierarchy`, `find_gameobjects`, `get_gameobject`, `open_scene`, `save_scene`, `new_scene` |
| GameObjects | `create_gameobject`, `delete_gameobject`, `duplicate_gameobject`, `set_transform`, `set_parent`, `set_active`, `select` |
| Componentes | `add_component`, `remove_component`, `set_property` |
| Assets | `list_assets`, `read_text_asset`, `write_text_asset`, `create_material`, `instantiate_prefab`, `create_prefab` |
| Editor | `play_mode`, `get_console_logs`, `clear_console`, `execute_menu_item` |

Toda alteração na cena passa pelo **Undo** do Unity, então dá para desfazer com Ctrl+Z.
Para indicar um objeto (`target`), use o instanceId, o caminho (`Ambiente/Arvore`) ou um nome único.

### Exemplos de pedidos
- "Crie um chão 20×20, um cubo vermelho com Rigidbody caindo de 5 m de altura e salve a cena como Assets/Scenes/Teste.unity"
- "Escreva um script PlayerController com WASD e pulo, espere compilar e adicione ao objeto Player"
- "Dê Play, espere e me mostre os erros do console"

## Solução de problemas
- **"Não consegui falar com o Unity"**: o Editor precisa estar aberto. Confira **Tools › Claude MCP › Status**.
- **Tempo esgotado**: o Unity estava compilando, importando ou com uma janela de diálogo aberta. Tente de novo.
- **Porta em uso**: mude a porta (veja a seção 2).
- Depois que um script é escrito, o Unity recompila e a bridge reinicia sozinha. Isso leva alguns segundos.
