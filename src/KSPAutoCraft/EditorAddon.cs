using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace KSPAutoCraft
{
    [Serializable]
    internal sealed class Settings
    {
        public int port = 18080;
        public string token;
    }
    [Serializable]
    internal sealed class ErrorDetail { public string code; public string message; }
    [Serializable]
    internal sealed class ErrorBody { public ErrorDetail error; }
    [Serializable]
    internal sealed class Health
    {
        public string version = ReleaseInfo.Version;
        public string kspVersion;
        public string facility;
        public bool allowFileGeneration;
        public bool desktopWorkerRunning;
        public string desktopConnectionStatus;
        public string desktopStatus;
        public bool aiHubLoaded = HubDependency.Available;
        public string aiHubStatus = HubDependency.Status;
        public string[] capabilities = { "catalog", "ship-state", "validate-stack-plan", "generate-craft-file", "manual-load-with-backup", "contracts", "world-context", "surface-plan", "nested-json", "surface-geometry", "native-surface-rotation", "desktop-ui", "auto-health", "flight-catalog", "resolved-placements", "aircraft-geometry", "environment-sampling", "aihub-required" };
    }

    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public sealed class EditorAddon : MonoBehaviour
    {
        private const string LockId = "KSPAutoCraft.Window";
        private readonly MainThreadQueue queue = new MainThreadQueue();
        private LoopbackServer server;
        private DesktopPanel desktop;
        private Settings settings;
        private string dataFolder;
        private string candidate;
        private string candidateFacility;
        private string status = "Starting...";
        private bool allowGeneration;
        private bool confirmLoad;
        private bool collapsed;
        private Rect window = new Rect(340, 55, 530, 100);

        private void Start()
        {
            try
            {
                if (Versioning.version_major != 1 || Versioning.version_minor != 12 || Versioning.Revision != 5)
                    throw new InvalidOperationException("This build targets KSP 1.12.5 only.");
                dataFolder = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KSPAutoCraft", "PluginData");
                Directory.CreateDirectory(dataFolder);
                var path = Path.Combine(dataFolder, "settings.json");
                if (File.Exists(path)) settings = JsonUtility.FromJson<Settings>(File.ReadAllText(path, Encoding.UTF8));
                else
                {
                    var bytes = new byte[32];
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                    settings = new Settings { token = Convert.ToBase64String(bytes) };
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(ApiJson.Serialize(settings));
                }
                if (settings == null) throw new InvalidDataException("Invalid settings.json.");
                desktop = new DesktopPanel(dataFolder, KSPUtil.ApplicationRootPath, BuildCandidate);
                allowGeneration = desktop.AutoEnableGeneration;
                server = new LoopbackServer(settings.port, settings.token, queue.Send);
                server.Start();
                status = "本地 API 已启动；进入编辑器后自动检查连接。";
                Debug.Log("[KSPAutoCraft] Editor API listening on 127.0.0.1:" + settings.port);
            }
            catch (Exception error)
            {
                if (server != null) server.Dispose();
                server = null;
                status = "Startup failed (" + error.GetType().Name + "). Check version, port and settings.json.";
                Debug.LogError("[KSPAutoCraft] " + status);
            }
        }

        private void Update()
        {
            queue.Pump(Handle);
            if (desktop != null) desktop.Tick(server != null && Ready());
        }

        private void OnDestroy()
        {
            queue.Dispose();
            if (desktop != null) desktop.Dispose();
            if (server != null) server.Dispose();
            InputLockManager.RemoveControlLock(LockId);
        }

        private void OnDisable()
        {
            if (desktop != null) desktop.Cancel();
            InputLockManager.RemoveControlLock(LockId);
        }

        private void OnGUI()
        {
            window = GUILayout.Window(GetInstanceID(), window, DrawWindow, "KSP AutoCraft " + ReleaseInfo.Version + " — " + ReleaseInfo.Author, GUILayout.Width(530));
            bool inside = window.Contains(Event.current.mousePosition);
            if (!inside && Event.current.type == EventType.MouseDown) GUI.FocusControl(null);
            bool typing = (GUI.GetNameOfFocusedControl() ?? "").StartsWith("KSPAutoCraft.", StringComparison.Ordinal);
            if (inside || typing) InputLockManager.SetControlLock(ControlTypes.EDITOR_LOCK, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        private void DrawWindow(int id)
        {
            if (GUILayout.Button(collapsed ? "展开" : "收起")) { collapsed = !collapsed; window.height = 0; GUI.FocusControl(null); }
            if (!collapsed)
            {
                GUILayout.Label(status);
                if (server != null)
                {
                    GUILayout.Label("API: http://127.0.0.1:" + settings.port + "/v1");
                    if (desktop != null) allowGeneration = desktop.Draw(allowGeneration);
                    if (candidate != null)
                    {
                        GUI.enabled = desktop == null || !desktop.Busy;
                        GUILayout.Label("Candidate: " + Path.GetFileName(candidate));
                        if (!confirmLoad && GUILayout.Button("Review / load candidate")) confirmLoad = true;
                        if (confirmLoad)
                        {
                            GUILayout.Label("Replace the editor ship? A backup will be saved first. Unsaved held parts are not included.");
                            if (GUILayout.Button("Back up current ship and load")) LoadCandidate();
                            if (GUILayout.Button("Cancel")) confirmLoad = false;
                        }
                        GUI.enabled = true;
                    }
                }
            }
            GUI.DragWindow();
        }

        private void LoadCandidate()
        {
            confirmLoad = false;
            try
            {
                if (!Ready() || EditorLogic.SelectedPart != null) throw new InvalidOperationException("Finish placing or discard the held part first.");
                if (EditorDriver.editorFacility.ToString() != candidateFacility) throw new InvalidOperationException("Candidate facility does not match this editor.");
                if (!File.Exists(candidate)) throw new FileNotFoundException("Candidate is no longer present.");
                string missing = "";
                if (!ShipConstruction.AllPartsFound(ConfigNode.Load(candidate), ref missing))
                    throw new InvalidOperationException("Candidate contains unavailable parts.");
                if (EditorLogic.fetch.ship.parts.Count > 0)
                    KspAdapter.SaveNew(EditorLogic.fetch.ship.SaveShip(), Path.Combine(dataFolder, "Backups"), "backup");
                // No network endpoint invokes this destructive editor action.
                EditorLogic.LoadShipFromFile(candidate);
                status = "Load requested. Check the ship and staging before saving or launching.";
            }
            catch (Exception error)
            {
                status = "Load stopped: " + error.GetType().Name + ". Check held parts, facility, and PluginData/Backups.";
                Debug.LogError("[KSPAutoCraft] " + status);
            }
        }

        private static bool Ready()
        {
            return HighLogic.LoadedSceneIsEditor && HighLogic.CurrentGame != null && EditorLogic.fetch != null &&
                EditorLogic.fetch.ship != null && PartLoader.LoadedPartsList != null;
        }

        private PlanResult BuildCandidate(CraftPlan plan)
        {
            if (!Ready()) throw new PlanException("编辑器尚未就绪。");
            if (!allowGeneration) throw new PlanException("请开启面板中的允许生成候选文件。设计 JSON 已保留。");
            var result = KspAdapter.Validate(plan);
            if (plan.facility != EditorDriver.editorFacility.ToString()) throw new PlanException("设计与当前装配厂类型不匹配。");
            candidate = KspAdapter.SaveNew(KspAdapter.CraftConfig(plan, result), Path.Combine(dataFolder, "Builds"), "candidate");
            candidateFacility = plan.facility;
            confirmLoad = false;
            result.craftFile = Path.GetFileName(candidate);
            status = "候选已生成。请检查分级、姿态和任务要求，再确认载入。";
            return result;
        }

        private static ApiResponse Json(object body, int statusCode = 200) { return new ApiResponse(statusCode, ApiJson.Serialize(body)); }
        private static ApiResponse Error(int statusCode, string code, string message)
        {
            return Json(new ErrorBody { error = new ErrorDetail { code = code, message = message } }, statusCode);
        }

        private ApiResponse Handle(ApiRequest request)
        {
            try
            {
                if (!Ready()) return Error(503, "editor_not_ready", "Wait until the VAB/SPH has finished loading.");
                var uri = new Uri("http://127.0.0.1" + request.Path);
                if (request.Method == "GET")
                {
                    if (uri.AbsolutePath.StartsWith("/v1/contracts/", StringComparison.Ordinal))
                    {
                        Guid id;
                        if (uri.Query.Length != 0 || !Guid.TryParse(uri.AbsolutePath.Substring("/v1/contracts/".Length), out id))
                            return Error(400, "invalid_contract_id", "Expected a contract GUID without query parameters.");
                        var detail = ContractAdapter.Detail(id);
                        return detail == null ? Error(404, "contract_not_found", "Contract is not in this save's current contract list.") : Json(detail);
                    }
                    switch (uri.AbsolutePath)
                    {
                        case "/v1/health": return Json(new Health {
                            kspVersion = Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision,
                            facility = EditorDriver.editorFacility.ToString(), allowFileGeneration = allowGeneration,
                            desktopWorkerRunning = desktop != null && desktop.Busy,
                            desktopConnectionStatus = desktop == null ? "" : desktop.ConnectionStatus,
                            desktopStatus = desktop == null ? "" : desktop.Status
                        });
                        case "/v1/ship": return Json(KspAdapter.State());
                        case "/v1/world": return Json(ContractAdapter.World());
                        case "/v1/environment":
                            var eq = Query(uri.Query, "body", "altitude");
                            string bodyName, altitudeText;
                            double altitude;
                            if (!eq.TryGetValue("body", out bodyName) || !eq.TryGetValue("altitude", out altitudeText) ||
                                !double.TryParse(altitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out altitude)) throw new PlanException("body and altitude are required.");
                            return Json(FlightCatalog.Environment(bodyName, altitude));
                        case "/v1/contracts":
                            var cq = Query(uri.Query, "offset", "limit", "state");
                            string state;
                            if (!cq.TryGetValue("state", out state)) state = "active";
                            if (state != "active" && state != "offered" && state != "all") throw new PlanException("state must be active, offered or all.");
                            return Json(ContractAdapter.List(state, Number(cq, "offset", 0, 0, int.MaxValue), Number(cq, "limit", 50, 1, 100)));
                        case "/v1/catalog":
                            var query = Query(uri.Query, "offset", "limit", "search");
                            int offset = Number(query, "offset", 0, 0, int.MaxValue);
                            int limit = Number(query, "limit", 100, 1, 200);
                            string search;
                            query.TryGetValue("search", out search);
                            return Json(KspAdapter.Catalog(offset, limit, search));
                    }
                }
                if (request.Method == "POST" && (uri.AbsolutePath == "/v1/validate" || uri.AbsolutePath == "/v1/build"))
                {
                    if (uri.Query.Length != 0) return Error(400, "invalid_query", "Plan endpoints do not accept query parameters.");
                    CraftPlan plan;
                    try { plan = PlanJson.Read(request.Body); }
                    catch (FormatException error) { return Error(400, "invalid_json", error.Message); }
                    var result = KspAdapter.Validate(plan);
                    if (uri.AbsolutePath == "/v1/build")
                    {
                        if (!allowGeneration) return Error(403, "generation_disabled", "Enable Allow file generation in the in-game AutoCraft window.");
                        if (plan.facility != EditorDriver.editorFacility.ToString()) return Error(409, "facility_mismatch", "Open the facility specified by the plan.");
                        result = BuildCandidate(plan);
                    }
                    return Json(result);
                }
                return Error(404, "not_found", "Unsupported API route or method.");
            }
            catch (PlanException error) { return Error(422, "invalid_plan", error.Message); }
            catch (Exception error)
            {
                Debug.LogError("[KSPAutoCraft] API operation failed: " + error.GetType().Name);
                return Error(500, "operation_failed", "Operation failed. Check KSP.log; mod-specific behavior may be unsupported. Do not automatically retry builds.");
            }
        }

        private static Dictionary<string, string> Query(string query, params string[] allowed)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (query.Length == 0) return values;
            foreach (var item in query.Substring(1).Split('&'))
            {
                var pair = item.Split(new[] { '=' }, 2);
                string key = Uri.UnescapeDataString(pair[0]);
                if (Array.IndexOf(allowed, key) < 0 || values.ContainsKey(key)) throw new PlanException("Unknown or duplicate query parameter.");
                values.Add(key, pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : "");
            }
            return values;
        }

        private static int Number(Dictionary<string, string> query, string key, int fallback, int min, int max)
        {
            string text;
            if (!query.TryGetValue(key, out text)) return fallback;
            int value;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < min || value > max)
                throw new PlanException("Invalid " + key + " query parameter.");
            return value;
        }
    }
}
