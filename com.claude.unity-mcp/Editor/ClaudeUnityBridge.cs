// Servidor HTTP local dentro do Editor Unity. Recebe comandos do servidor MCP
// (POST http://127.0.0.1:8090/command  {"command": "...", "params": {...}})
// e os executa na thread principal do Editor.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace ClaudeUnityMcp
{
    [InitializeOnLoad]
    public static class ClaudeUnityBridge
    {
        public const string PortPrefKey = "ClaudeUnityMcp.Port";
        public const string EnabledPrefKey = "ClaudeUnityMcp.Enabled";

        static HttpListener _listener;
        static Thread _thread;
        static volatile bool _running;

        class Job
        {
            public string Command;
            public Dictionary<string, object> Params;
            public object Result;
            public string Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static readonly ConcurrentQueue<Job> _queue = new ConcurrentQueue<Job>();

        public static int Port => EditorPrefs.GetInt(PortPrefKey, 8090);
        public static bool Enabled => EditorPrefs.GetBool(EnabledPrefKey, true);
        public static bool IsRunning => _running;

        static ClaudeUnityBridge()
        {
            LogBuffer.Init();
            EditorApplication.update += ProcessQueue;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            if (Enabled) Start();
        }

        public static void Start()
        {
            if (_running) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _running = true;
                _thread = new Thread(ListenLoop) { IsBackground = true, Name = "ClaudeUnityMcp" };
                _thread.Start();
                Debug.Log($"[Claude MCP] Bridge ativa em http://127.0.0.1:{Port}/");
            }
            catch (Exception e)
            {
                _running = false;
                Debug.LogError($"[Claude MCP] Não foi possível iniciar na porta {Port}: {e.Message}");
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }

        static void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            var resp = new Dictionary<string, object>();
            int status = 200;
            try
            {
                var req = ctx.Request;
                // Só aceita conexões locais.
                if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address)) { status = 403; throw new Exception("Somente localhost"); }

                if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/health")
                {
                    resp["ok"] = true;
                    resp["unity"] = Application.unityVersion;
                }
                else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/command")
                {
                    string body;
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                    var json = MiniJson.Parse(body) as Dictionary<string, object>;
                    if (json == null || !json.ContainsKey("command")) throw new Exception("Corpo JSON precisa de 'command'");

                    var job = new Job
                    {
                        Command = json["command"] as string,
                        Params = (json.TryGetValue("params", out var p) ? p as Dictionary<string, object> : null)
                                 ?? new Dictionary<string, object>()
                    };
                    _queue.Enqueue(job);

                    if (!job.Done.Wait(TimeSpan.FromSeconds(60)))
                        throw new Exception("Tempo esgotado esperando o Editor (ele pode estar compilando ou com um diálogo aberto).");

                    if (job.Error != null) { resp["ok"] = false; resp["error"] = job.Error; }
                    else { resp["ok"] = true; resp["result"] = job.Result; }
                }
                else { status = 404; throw new Exception("Rota não encontrada"); }
            }
            catch (Exception e)
            {
                if (status == 200) status = 500;
                resp["ok"] = false;
                resp["error"] = e.Message;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(MiniJson.Serialize(resp));
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        static void ProcessQueue()
        {
            int budget = 20;
            while (budget-- > 0 && _queue.TryDequeue(out var job))
            {
                try { job.Result = Commands.Execute(job.Command, job.Params); }
                catch (Exception e) { job.Error = e.InnerException?.Message ?? e.Message; }
                finally { job.Done.Set(); }
            }
        }

        // ---------------- Menu ----------------
        [MenuItem("Tools/Claude MCP/Iniciar bridge")]
        static void MenuStart() { EditorPrefs.SetBool(EnabledPrefKey, true); Start(); }

        [MenuItem("Tools/Claude MCP/Parar bridge")]
        static void MenuStop() { EditorPrefs.SetBool(EnabledPrefKey, false); Stop(); Debug.Log("[Claude MCP] Bridge parada."); }

        [MenuItem("Tools/Claude MCP/Status")]
        static void MenuStatus()
        {
            EditorUtility.DisplayDialog("Claude MCP",
                _running ? $"Ativa em http://127.0.0.1:{Port}/" : "Parada", "OK");
        }
    }

    // Guarda as últimas mensagens do Console para o comando get_console_logs.
    public static class LogBuffer
    {
        public struct Entry { public string type, message, stack, time; }
        static readonly object _lock = new object();
        static readonly List<Entry> _entries = new List<Entry>();
        const int Max = 1000;
        static bool _init;

        public static void Init()
        {
            if (_init) return;
            _init = true;
            Application.logMessageReceivedThreaded += (msg, stack, type) =>
            {
                lock (_lock)
                {
                    _entries.Add(new Entry { type = type.ToString(), message = msg, stack = stack, time = DateTime.Now.ToString("HH:mm:ss") });
                    if (_entries.Count > Max) _entries.RemoveAt(0);
                }
            };
        }

        public static List<Entry> Get(int count, string type)
        {
            lock (_lock)
            {
                var res = new List<Entry>();
                for (int i = _entries.Count - 1; i >= 0 && res.Count < count; i--)
                {
                    var e = _entries[i];
                    if (string.IsNullOrEmpty(type) || string.Equals(e.type, type, StringComparison.OrdinalIgnoreCase)
                        || (type.Equals("Error", StringComparison.OrdinalIgnoreCase) && (e.type == "Exception" || e.type == "Assert")))
                        res.Add(e);
                }
                res.Reverse();
                return res;
            }
        }

        public static void Clear() { lock (_lock) _entries.Clear(); }
    }
}
