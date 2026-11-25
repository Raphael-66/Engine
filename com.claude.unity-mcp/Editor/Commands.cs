// Implementação dos comandos expostos ao Claude. Tudo roda na thread principal do Editor.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ClaudeUnityMcp
{
    public static class Commands
    {
        delegate object Handler(Dictionary<string, object> p);

        static readonly Dictionary<string, Handler> _handlers = new Dictionary<string, Handler>
        {
            { "get_project_info", GetProjectInfo },
            { "get_hierarchy", GetHierarchy },
            { "find_gameobjects", FindGameObjects },
            { "get_gameobject", GetGameObject },
            { "create_gameobject", CreateGameObject },
            { "delete_gameobject", DeleteGameObject },
            { "duplicate_gameobject", DuplicateGameObject },
            { "set_transform", SetTransform },
            { "set_parent", SetParent },
            { "set_active", SetActive },
            { "add_component", AddComponent },
            { "remove_component", RemoveComponent },
            { "set_property", SetProperty },
            { "list_assets", ListAssets },
            { "read_text_asset", ReadTextAsset },
            { "write_text_asset", WriteTextAsset },
            { "create_material", CreateMaterial },
            { "instantiate_prefab", InstantiatePrefab },
            { "create_prefab", CreatePrefab },
            { "open_scene", OpenScene },
            { "save_scene", SaveScene },
            { "new_scene", NewScene },
            { "play_mode", PlayModeCmd },
            { "get_console_logs", GetConsoleLogs },
            { "clear_console", ClearConsole },
            { "execute_menu_item", ExecuteMenuItem },
            { "select", SelectObj },
            { "refresh_assets", RefreshAssets },
            { "get_compile_status", GetCompileStatus },
        };

        public static object Execute(string command, Dictionary<string, object> p)
        {
            if (command == null || !_handlers.TryGetValue(command, out var h))
                throw new Exception($"Comando desconhecido: {command}. Disponíveis: {string.Join(", ", _handlers.Keys)}");
            return h(p);
        }

        // ======================= Helpers =======================
        static string Str(Dictionary<string, object> p, string k, string def = null)
            => p.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;

        static bool Bool(Dictionary<string, object> p, string k, bool def = false)
            => p.TryGetValue(k, out var v) && v != null ? Convert.ToBoolean(v, CultureInfo.InvariantCulture) : def;

        static int Int(Dictionary<string, object> p, string k, int def = 0)
            => p.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : def;

        static string Req(Dictionary<string, object> p, string k)
            => Str(p, k) ?? throw new Exception($"Parâmetro obrigatório ausente: '{k}'");

        static float F(object o) => Convert.ToSingle(o, CultureInfo.InvariantCulture);

        static Vector4 Vec(object o, Vector4 def)
        {
            if (o is List<object> l)
                return new Vector4(l.Count > 0 ? F(l[0]) : def.x, l.Count > 1 ? F(l[1]) : def.y,
                                   l.Count > 2 ? F(l[2]) : def.z, l.Count > 3 ? F(l[3]) : def.w);
            if (o is Dictionary<string, object> d)
            {
                float G(string a, string b, float dv) => d.TryGetValue(a, out var v) || d.TryGetValue(b, out v) ? F(v) : dv;
                return new Vector4(G("x", "r", def.x), G("y", "g", def.y), G("z", "b", def.z), G("w", "a", def.w));
            }
            throw new Exception("Vetor esperado como [x,y,z] ou {\"x\":..,\"y\":..}");
        }

        static bool TryVec3(Dictionary<string, object> p, string k, out Vector3 v)
        {
            v = default;
            if (!p.TryGetValue(k, out var o) || o == null) return false;
            v = Vec(o, Vector4.zero);
            return true;
        }

        static Color ParseColor(object o)
        {
            if (o is string s)
            {
                if (ColorUtility.TryParseHtmlString(s, out var c)) return c;
                throw new Exception($"Cor inválida: {s}");
            }
            Vector4 v = Vec(o, new Vector4(0, 0, 0, 1));
            if (v.x > 1 || v.y > 1 || v.z > 1) v = new Vector4(v.x / 255f, v.y / 255f, v.z / 255f, v.w > 1 ? v.w / 255f : v.w);
            return new Color(v.x, v.y, v.z, v.w);
        }

        static List<object> V3(Vector3 v) => new List<object> { v.x, v.y, v.z };

        static IEnumerable<GameObject> AllSceneObjects()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                foreach (var root in sc.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        yield return t.gameObject;
            }
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Resolve um GameObject por instanceId, caminho "Pai/Filho" ou nome (inclui inativos).</summary>
        static GameObject Resolve(string target)
        {
            if (string.IsNullOrEmpty(target)) throw new Exception("Parâmetro 'target' obrigatório");
            if (int.TryParse(target, out int id))
            {
                var o = EditorUtility.InstanceIDToObject(id);
                if (o is GameObject g) return g;
                if (o is Component c) return c.gameObject;
            }
            var all = AllSceneObjects().ToList();
            var byPath = all.FirstOrDefault(g => PathOf(g.transform) == target);
            if (byPath != null) return byPath;
            var byName = all.Where(g => g.name == target).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1)
                throw new Exception($"'{target}' é ambíguo ({byName.Count} objetos). Use o caminho completo ou o instanceId: " +
                                    string.Join("; ", byName.Take(10).Select(g => $"{PathOf(g.transform)} (id {g.GetInstanceID()})")));
            throw new Exception($"GameObject não encontrado: '{target}'");
        }

        static Type FindType(string name, Type mustBe)
        {
            var t = Type.GetType(name);
            if (t != null && mustBe.IsAssignableFrom(t)) return t;
            Type best = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                foreach (var ty in types)
                {
                    if (!mustBe.IsAssignableFrom(ty) || ty.IsAbstract) continue;
                    if (ty.FullName == name) return ty;
                    if (ty.Name == name && (best == null || ty.Namespace == "UnityEngine")) best = ty;
                }
            }
            return best ?? throw new Exception($"Tipo não encontrado: '{name}'. Se for um script novo, verifique se compilou sem erros.");
        }

        static Component FindComponent(GameObject go, string typeName)
        {
            var c = go.GetComponents<Component>().FirstOrDefault(x => x != null &&
                       (x.GetType().Name == typeName || x.GetType().FullName == typeName));
            return c ?? throw new Exception($"'{go.name}' não tem componente '{typeName}'. Tem: " +
                                            string.Join(", ", go.GetComponents<Component>().Where(x => x).Select(x => x.GetType().Name)));
        }

        static Dictionary<string, object> Brief(GameObject g) => new Dictionary<string, object>
        {
            { "name", g.name }, { "id", g.GetInstanceID() }, { "path", PathOf(g.transform) }, { "active", g.activeSelf }
        };

        static void EnsureAssetFolder(string assetPath)
        {
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir)) return;
            var parts = dir.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        static string CheckAssetPath(string path)
        {
            path = path.Replace('\\', '/');
            if (!(path.StartsWith("Assets/") || path == "Assets" || path.StartsWith("Packages/")) || path.Contains(".."))
                throw new Exception("O caminho precisa começar com 'Assets/' (ou 'Packages/') e não pode conter '..'");
            return path;
        }

        // ======================= Projeto / cena =======================
        static object GetProjectInfo(Dictionary<string, object> p)
        {
            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                scenes.Add(new Dictionary<string, object> { { "name", s.name }, { "path", s.path }, { "isDirty", s.isDirty }, { "isLoaded", s.isLoaded } });
            }
            return new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "productName", PlayerSettings.productName },
                { "companyName", PlayerSettings.companyName },
                { "projectPath", Path.GetDirectoryName(Application.dataPath) },
                { "platform", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "renderPipeline", UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in" },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isCompiling", EditorApplication.isCompiling },
                { "openScenes", scenes },
                { "buildScenes", EditorBuildSettings.scenes.Select(s => (object)s.path).ToList() },
            };
        }

        static object NodeOf(Transform t, int depth, int maxDepth, bool comps)
        {
            var d = Brief(t.gameObject);
            if (comps) d["components"] = t.GetComponents<Component>().Where(c => c).Select(c => (object)c.GetType().Name).ToList();
            if (t.childCount > 0)
            {
                if (depth < maxDepth)
                {
                    var ch = new List<object>();
                    foreach (Transform c in t) ch.Add(NodeOf(c, depth + 1, maxDepth, comps));
                    d["children"] = ch;
                }
                else d["childCount"] = t.childCount;
            }
            return d;
        }

        static object GetHierarchy(Dictionary<string, object> p)
        {
            int maxDepth = Int(p, "max_depth", 10);
            bool comps = Bool(p, "include_components", true);
            string root = Str(p, "root");
            if (!string.IsNullOrEmpty(root)) return NodeOf(Resolve(root).transform, 0, maxDepth, comps);

            var res = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                res.Add(new Dictionary<string, object>
                {
                    { "scene", s.name },
                    { "roots", s.GetRootGameObjects().Select(g => NodeOf(g.transform, 0, maxDepth, comps)).ToList() }
                });
            }
            return res;
        }

        static object FindGameObjects(Dictionary<string, object> p)
        {
            string name = Str(p, "name"), tag = Str(p, "tag"), comp = Str(p, "component");
            int limit = Int(p, "limit", 100);
            var q = AllSceneObjects();
            if (!string.IsNullOrEmpty(name)) q = q.Where(g => g.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(tag)) q = q.Where(g => g.CompareTag(tag));
            if (!string.IsNullOrEmpty(comp)) q = q.Where(g => g.GetComponents<Component>().Any(c => c && c.GetType().Name == comp));
            return q.Take(limit).Select(g => (object)Brief(g)).ToList();
        }

        static object SerializeComponent(Component c, bool full)
        {
            var d = new Dictionary<string, object> { { "type", c.GetType().Name }, { "id", c.GetInstanceID() } };
            if (c is Behaviour b) d["enabled"] = b.enabled;
            if (!full) return d;
            var props = new Dictionary<string, object>();
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            bool enter = true;
            int n = 0;
            while (it.NextVisible(enter) && n++ < 200)
            {
                enter = false;
                if (it.name == "m_Script") continue;
                props[it.propertyPath] = PropValue(it);
            }
            d["properties"] = props;
            return d;
        }

        static object PropValue(SerializedProperty sp)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: return sp.longValue;
                case SerializedPropertyType.Boolean: return sp.boolValue;
                case SerializedPropertyType.Float: return sp.doubleValue;
                case SerializedPropertyType.String: return sp.stringValue;
                case SerializedPropertyType.Color: var c = sp.colorValue; return new List<object> { c.r, c.g, c.b, c.a };
                case SerializedPropertyType.Vector2: return new List<object> { sp.vector2Value.x, sp.vector2Value.y };
                case SerializedPropertyType.Vector3: return V3(sp.vector3Value);
                case SerializedPropertyType.Vector4: var v = sp.vector4Value; return new List<object> { v.x, v.y, v.z, v.w };
                case SerializedPropertyType.Quaternion: return V3(sp.quaternionValue.eulerAngles);
                case SerializedPropertyType.Enum:
                    return sp.enumValueIndex >= 0 && sp.enumValueIndex < sp.enumNames.Length ? sp.enumNames[sp.enumValueIndex] : (object)sp.intValue;
                case SerializedPropertyType.ObjectReference:
                    var o = sp.objectReferenceValue;
                    if (!o) return null;
                    var ap = AssetDatabase.GetAssetPath(o);
                    return string.IsNullOrEmpty(ap) ? $"{o.name} (id {o.GetInstanceID()})" : ap;
                case SerializedPropertyType.LayerMask: return sp.intValue;
                case SerializedPropertyType.Rect: var r = sp.rectValue; return new List<object> { r.x, r.y, r.width, r.height };
                default:
                    if (sp.isArray) return $"<{sp.arrayElementType}[{sp.arraySize}]>";
                    return $"<{sp.propertyType}>";
            }
        }

        static object GetGameObject(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            bool full = Bool(p, "include_properties", true);
            var d = Brief(g);
            d["tag"] = g.tag;
            d["layer"] = LayerMask.LayerToName(g.layer);
            d["isStatic"] = g.isStatic;
            d["position"] = V3(g.transform.position);
            d["localPosition"] = V3(g.transform.localPosition);
            d["rotation"] = V3(g.transform.eulerAngles);
            d["localScale"] = V3(g.transform.localScale);
            d["parent"] = g.transform.parent ? PathOf(g.transform.parent) : null;
            d["children"] = g.transform.Cast<Transform>().Select(t => (object)t.name).ToList();
            d["components"] = g.GetComponents<Component>().Where(c => c && !(c is Transform)).Select(c => SerializeComponent(c, full)).ToList();
            var src = PrefabUtility.GetCorrespondingObjectFromSource(g);
            if (src) d["prefab"] = AssetDatabase.GetAssetPath(src);
            return d;
        }

        // ======================= GameObjects =======================
        static void ApplyTransform(Transform t, Dictionary<string, object> p, bool local)
        {
            if (TryVec3(p, "position", out var pos)) { if (local) t.localPosition = pos; else t.position = pos; }
            if (TryVec3(p, "rotation", out var rot)) { if (local) t.localEulerAngles = rot; else t.eulerAngles = rot; }
            if (TryVec3(p, "scale", out var sc)) t.localScale = sc;
        }

        static object CreateGameObject(Dictionary<string, object> p)
        {
            string name = Str(p, "name", "GameObject");
            string prim = Str(p, "primitive");
            GameObject g;
            if (!string.IsNullOrEmpty(prim))
            {
                if (!Enum.TryParse(prim, true, out PrimitiveType pt))
                    throw new Exception("primitive deve ser: Cube, Sphere, Capsule, Cylinder, Plane, Quad");
                g = GameObject.CreatePrimitive(pt);
                g.name = name;
            }
            else g = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(g, "Claude: criar " + name);

            string parent = Str(p, "parent");
            if (!string.IsNullOrEmpty(parent)) g.transform.SetParent(Resolve(parent).transform, false);
            ApplyTransform(g.transform, p, Bool(p, "local", false));

            if (p.TryGetValue("components", out var comps) && comps is List<object> list)
                foreach (var c in list) Undo.AddComponent(g, FindType(c.ToString(), typeof(Component)));

            string tag = Str(p, "tag");
            if (!string.IsNullOrEmpty(tag)) g.tag = tag;

            EditorSceneManager.MarkSceneDirty(g.scene);
            Selection.activeGameObject = g;
            return Brief(g);
        }

        static object DeleteGameObject(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            var info = Brief(g);
            var scene = g.scene;
            Undo.DestroyObjectImmediate(g);
            EditorSceneManager.MarkSceneDirty(scene);
            info["deleted"] = true;
            return info;
        }

        static object DuplicateGameObject(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            var copy = Object.Instantiate(g, g.transform.parent);
            copy.name = Str(p, "new_name", g.name + " (1)");
            Undo.RegisterCreatedObjectUndo(copy, "Claude: duplicar");
            ApplyTransform(copy.transform, p, Bool(p, "local", false));
            EditorSceneManager.MarkSceneDirty(copy.scene);
            return Brief(copy);
        }

        static object SetTransform(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            Undo.RecordObject(g.transform, "Claude: transform");
            ApplyTransform(g.transform, p, Bool(p, "local", false));
            EditorSceneManager.MarkSceneDirty(g.scene);
            return new Dictionary<string, object>
            {
                { "position", V3(g.transform.position) }, { "rotation", V3(g.transform.eulerAngles) }, { "localScale", V3(g.transform.localScale) }
            };
        }

        static object SetParent(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            string parent = Str(p, "parent");
            Transform pt = string.IsNullOrEmpty(parent) ? null : Resolve(parent).transform;
            Undo.SetTransformParent(g.transform, pt, "Claude: parent");
            if (!Bool(p, "keep_world_position", true) && pt) g.transform.localPosition = Vector3.zero;
            EditorSceneManager.MarkSceneDirty(g.scene);
            return Brief(g);
        }

        static object SetActive(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            Undo.RecordObject(g, "Claude: active");
            g.SetActive(Bool(p, "active", true));
            EditorSceneManager.MarkSceneDirty(g.scene);
            return Brief(g);
        }

        // ======================= Componentes =======================
        static object AddComponent(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            var t = FindType(Req(p, "component"), typeof(Component));
            var c = Undo.AddComponent(g, t);
            if (!c) throw new Exception($"Não foi possível adicionar {t.Name} (já existe ou é incompatível?)");
            EditorSceneManager.MarkSceneDirty(g.scene);
            return SerializeComponent(c, true);
        }

        static object RemoveComponent(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            var c = FindComponent(g, Req(p, "component"));
            if (c is Transform) throw new Exception("Transform não pode ser removido");
            Undo.DestroyObjectImmediate(c);
            EditorSceneManager.MarkSceneDirty(g.scene);
            return new Dictionary<string, object> { { "removed", true } };
        }

        static object SetProperty(Dictionary<string, object> p)
        {
            string target = Req(p, "target");
            string prop = Req(p, "property");
            p.TryGetValue("value", out var value);

            Object obj;
            string compName = Str(p, "component");
            if (target.StartsWith("Assets/"))
            {
                obj = AssetDatabase.LoadMainAssetAtPath(target) ?? throw new Exception("Asset não encontrado: " + target);
                if (!string.IsNullOrEmpty(compName) && obj is GameObject pg) obj = FindComponent(pg, compName);
            }
            else
            {
                var g = Resolve(target);
                obj = string.IsNullOrEmpty(compName) ? (Object)g : FindComponent(g, compName);
            }

            var so = new SerializedObject(obj);
            var sp = so.FindProperty(prop) ?? so.FindProperty("m_" + char.ToUpper(prop[0]) + prop.Substring(1));
            if (sp != null)
            {
                AssignProp(sp, value);
                so.ApplyModifiedProperties();
            }
            else
            {
                // Fallback: propriedade/campo público C# via reflexão (ex.: Light.intensity, Rigidbody.mass)
                Undo.RecordObject(obj, "Claude: set " + prop);
                var type = obj.GetType();
                var pi = type.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                var fi = type.GetField(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Type vt = pi?.PropertyType ?? fi?.FieldType ?? throw new Exception(
                    $"Propriedade '{prop}' não encontrada em {type.Name}. Use get_gameobject para ver os nomes serializados.");
                object conv = ConvertTo(value, vt);
                if (pi != null) pi.SetValue(obj, conv); else fi.SetValue(obj, conv);
                EditorUtility.SetDirty(obj);
            }
            if (obj is Component cc && cc.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(cc.gameObject.scene);
            else if (obj is GameObject gg && gg.scene.IsValid()) EditorSceneManager.MarkSceneDirty(gg.scene);
            else AssetDatabase.SaveAssets();
            return new Dictionary<string, object> { { "ok", true }, { "object", obj.name }, { "property", prop } };
        }

        static Object LoadRef(object value, Type t)
        {
            if (value == null) return null;
            string s = value.ToString();
            if (s.StartsWith("Assets/") || s.StartsWith("Packages/"))
            {
                var all = AssetDatabase.LoadAllAssetsAtPath(s);
                var match = all.FirstOrDefault(a => t == null || t.IsInstanceOfType(a)) ?? AssetDatabase.LoadMainAssetAtPath(s);
                return match ?? throw new Exception("Asset não encontrado: " + s);
            }
            var g = Resolve(s);
            if (t != null && typeof(Component).IsAssignableFrom(t))
            {
                var comp = g.GetComponent(t);
                if (!comp) throw new Exception($"'{s}' não tem componente {t.Name}");
                return comp;
            }
            return g;
        }

        static void AssignProp(SerializedProperty sp, object v)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: sp.longValue = Convert.ToInt64(v, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.LayerMask: sp.intValue = Convert.ToInt32(v, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Boolean: sp.boolValue = Convert.ToBoolean(v, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Float: sp.doubleValue = Convert.ToDouble(v, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.String: sp.stringValue = v?.ToString() ?? ""; break;
                case SerializedPropertyType.Color: sp.colorValue = ParseColor(v); break;
                case SerializedPropertyType.Vector2: sp.vector2Value = Vec(v, Vector4.zero); break;
                case SerializedPropertyType.Vector3: sp.vector3Value = Vec(v, Vector4.zero); break;
                case SerializedPropertyType.Vector4: sp.vector4Value = Vec(v, Vector4.zero); break;
                case SerializedPropertyType.Quaternion: sp.quaternionValue = Quaternion.Euler(Vec(v, Vector4.zero)); break;
                case SerializedPropertyType.Rect: var r = Vec(v, Vector4.zero); sp.rectValue = new Rect(r.x, r.y, r.z, r.w); break;
                case SerializedPropertyType.Enum:
                    if (v is string es)
                    {
                        int idx = Array.FindIndex(sp.enumNames, n => string.Equals(n, es, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0) idx = Array.FindIndex(sp.enumDisplayNames, n => string.Equals(n, es, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0) throw new Exception($"Valor de enum inválido '{es}'. Opções: {string.Join(", ", sp.enumNames)}");
                        sp.enumValueIndex = idx;
                    }
                    else sp.intValue = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.ObjectReference: sp.objectReferenceValue = LoadRef(v, null); break;
                default: throw new Exception($"Tipo de propriedade não suportado: {sp.propertyType}");
            }
        }

        static object ConvertTo(object v, Type t)
        {
            if (v == null) return null;
            if (t == typeof(Vector3)) return (Vector3)Vec(v, Vector4.zero);
            if (t == typeof(Vector2)) return (Vector2)Vec(v, Vector4.zero);
            if (t == typeof(Vector4)) return Vec(v, Vector4.zero);
            if (t == typeof(Color)) return ParseColor(v);
            if (t == typeof(Quaternion)) return Quaternion.Euler(Vec(v, Vector4.zero));
            if (t.IsEnum) return v is string s ? Enum.Parse(t, s, true) : Enum.ToObject(t, Convert.ToInt32(v));
            if (typeof(Object).IsAssignableFrom(t)) return LoadRef(v, t);
            return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        }

        // ======================= Assets =======================
        static object ListAssets(Dictionary<string, object> p)
        {
            string filter = Str(p, "filter", "");
            string folder = CheckAssetPath(Str(p, "folder", "Assets"));
            int limit = Int(p, "limit", 200);
            var guids = AssetDatabase.FindAssets(filter, new[] { folder });
            return guids.Take(limit).Select(g =>
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                return (object)new Dictionary<string, object> { { "path", path }, { "type", t?.Name } };
            }).ToList();
        }

        static string FullPath(string assetPath)
            => Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);

        static object ReadTextAsset(Dictionary<string, object> p)
        {
            var path = CheckAssetPath(Req(p, "path"));
            var full = FullPath(path);
            if (!File.Exists(full)) throw new Exception("Arquivo não encontrado: " + path);
            return new Dictionary<string, object> { { "path", path }, { "content", File.ReadAllText(full) } };
        }

        static object WriteTextAsset(Dictionary<string, object> p)
        {
            var path = CheckAssetPath(Req(p, "path"));
            var content = Str(p, "content", "");
            EnsureAssetFolder(path);
            var full = FullPath(path);
            bool existed = File.Exists(full);
            File.WriteAllText(full, content);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            bool isScript = path.EndsWith(".cs");
            return new Dictionary<string, object>
            {
                { "path", path }, { "created", !existed },
                { "note", isScript ? "Script salvo. O Unity vai recompilar; use get_compile_status e get_console_logs para checar erros." : "Arquivo salvo." }
            };
        }

        static object CreateMaterial(Dictionary<string, object> p)
        {
            var path = CheckAssetPath(Req(p, "path"));
            if (!path.EndsWith(".mat")) path += ".mat";
            string shaderName = Str(p, "shader");
            Shader shader = !string.IsNullOrEmpty(shaderName) ? Shader.Find(shaderName) : null;
            if (shader == null)
            {
                var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                shader = rp && rp.defaultMaterial ? rp.defaultMaterial.shader : Shader.Find("Standard");
            }
            var mat = new Material(shader);
            if (p.TryGetValue("color", out var col) && col != null)
            {
                var c = ParseColor(col);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            }
            if (p.TryGetValue("metallic", out var m) && mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", F(m));
            if (p.TryGetValue("smoothness", out var s))
            {
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", F(s));
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", F(s));
            }
            EnsureAssetFolder(path);
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();

            string assign = Str(p, "assign_to");
            if (!string.IsNullOrEmpty(assign))
            {
                var r = Resolve(assign).GetComponent<Renderer>();
                if (!r) throw new Exception($"'{assign}' não tem Renderer");
                Undo.RecordObject(r, "Claude: material");
                r.sharedMaterial = mat;
                EditorSceneManager.MarkSceneDirty(r.gameObject.scene);
            }
            return new Dictionary<string, object> { { "path", path }, { "shader", shader.name } };
        }

        static object InstantiatePrefab(Dictionary<string, object> p)
        {
            var path = CheckAssetPath(Req(p, "path"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab) throw new Exception("Prefab não encontrado: " + path);
            var g = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(g, "Claude: instanciar prefab");
            string name = Str(p, "name");
            if (!string.IsNullOrEmpty(name)) g.name = name;
            string parent = Str(p, "parent");
            if (!string.IsNullOrEmpty(parent)) g.transform.SetParent(Resolve(parent).transform, false);
            ApplyTransform(g.transform, p, Bool(p, "local", false));
            EditorSceneManager.MarkSceneDirty(g.scene);
            return Brief(g);
        }

        static object CreatePrefab(Dictionary<string, object> p)
        {
            var g = Resolve(Req(p, "target"));
            var path = CheckAssetPath(Req(p, "path"));
            if (!path.EndsWith(".prefab")) path += ".prefab";
            EnsureAssetFolder(path);
            var asset = PrefabUtility.SaveAsPrefabAssetAndConnect(g, path, InteractionMode.UserAction);
            return new Dictionary<string, object> { { "path", path }, { "ok", asset != null } };
        }

        // ======================= Cenas =======================
        static object OpenScene(Dictionary<string, object> p)
        {
            var path = CheckAssetPath(Req(p, "path"));
            if (EditorApplication.isPlaying) throw new Exception("Saia do Play Mode antes de abrir uma cena");
            if (Bool(p, "save_current", true)) EditorSceneManager.SaveOpenScenes();
            var mode = Bool(p, "additive", false) ? OpenSceneMode.Additive : OpenSceneMode.Single;
            var s = EditorSceneManager.OpenScene(path, mode);
            return new Dictionary<string, object> { { "name", s.name }, { "path", s.path } };
        }

        static object SaveScene(Dictionary<string, object> p)
        {
            if (EditorApplication.isPlaying) throw new Exception("Não é possível salvar cenas em Play Mode");
            string path = Str(p, "path");
            var s = SceneManager.GetActiveScene();
            bool ok;
            if (!string.IsNullOrEmpty(path))
            {
                path = CheckAssetPath(path);
                if (!path.EndsWith(".unity")) path += ".unity";
                EnsureAssetFolder(path);
                ok = EditorSceneManager.SaveScene(s, path);
            }
            else if (string.IsNullOrEmpty(s.path))
                throw new Exception("A cena ainda não tem caminho; informe 'path', ex.: Assets/Scenes/Nova.unity");
            else ok = EditorSceneManager.SaveScene(s);
            return new Dictionary<string, object> { { "ok", ok }, { "path", s.path } };
        }

        static object NewScene(Dictionary<string, object> p)
        {
            if (EditorApplication.isPlaying) throw new Exception("Saia do Play Mode primeiro");
            if (Bool(p, "save_current", true)) EditorSceneManager.SaveOpenScenes();
            var setup = Bool(p, "empty", false) ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects;
            var s = EditorSceneManager.NewScene(setup, NewSceneMode.Single);
            string path = Str(p, "path");
            if (!string.IsNullOrEmpty(path))
            {
                path = CheckAssetPath(path);
                if (!path.EndsWith(".unity")) path += ".unity";
                EnsureAssetFolder(path);
                EditorSceneManager.SaveScene(s, path);
            }
            return new Dictionary<string, object> { { "name", s.name }, { "path", s.path } };
        }

        // ======================= Editor =======================
        static object PlayModeCmd(Dictionary<string, object> p)
        {
            switch (Req(p, "action").ToLowerInvariant())
            {
                case "play": EditorApplication.isPlaying = true; break;
                case "stop": EditorApplication.isPlaying = false; break;
                case "pause": EditorApplication.isPaused = !EditorApplication.isPaused; break;
                case "step": EditorApplication.Step(); break;
                default: throw new Exception("action deve ser play, stop, pause ou step");
            }
            return new Dictionary<string, object> { { "requested", p["action"] }, { "isPlaying", EditorApplication.isPlaying }, { "isPaused", EditorApplication.isPaused } };
        }

        static object GetConsoleLogs(Dictionary<string, object> p)
        {
            int count = Int(p, "count", 50);
            bool stack = Bool(p, "include_stack", false);
            return LogBuffer.Get(count, Str(p, "type")).Select(e =>
            {
                var d = new Dictionary<string, object> { { "time", e.time }, { "type", e.type }, { "message", e.message } };
                if (stack || e.type == "Exception") d["stack"] = e.stack;
                return (object)d;
            }).ToList();
        }

        static object ClearConsole(Dictionary<string, object> p)
        {
            LogBuffer.Clear();
            var t = Type.GetType("UnityEditor.LogEntries, UnityEditor") ?? Type.GetType("UnityEditorInternal.LogEntries, UnityEditor");
            t?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
            return new Dictionary<string, object> { { "cleared", true } };
        }

        static object ExecuteMenuItem(Dictionary<string, object> p)
        {
            var path = Req(p, "path");
            bool ok = EditorApplication.ExecuteMenuItem(path);
            if (!ok) throw new Exception($"Item de menu não encontrado ou indisponível: '{path}'");
            return new Dictionary<string, object> { { "executed", path } };
        }

        static object SelectObj(Dictionary<string, object> p)
        {
            var target = Req(p, "target");
            Object o = target.StartsWith("Assets/") ? AssetDatabase.LoadMainAssetAtPath(target) : Resolve(target);
            if (!o) throw new Exception("Não encontrado: " + target);
            Selection.activeObject = o;
            EditorGUIUtility.PingObject(o);
            if (Bool(p, "frame", true) && o is GameObject && SceneView.lastActiveSceneView) SceneView.lastActiveSceneView.FrameSelected();
            return new Dictionary<string, object> { { "selected", o.name } };
        }

        static object RefreshAssets(Dictionary<string, object> p)
        {
            AssetDatabase.Refresh();
            return new Dictionary<string, object> { { "refreshed", true }, { "isCompiling", EditorApplication.isCompiling } };
        }

        static object GetCompileStatus(Dictionary<string, object> p)
        {
            var errors = LogBuffer.Get(30, "Error").Where(e => e.message.Contains("error CS")).Select(e => (object)e.message).ToList();
            return new Dictionary<string, object>
            {
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "recentCompileErrors", errors }
            };
        }
    }
}
