using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KSPAutoCraft
{
    internal static class ReleaseInfo
    {
        internal const string Version = "0.6.1";
        internal const string Author = "fubuki11st";
    }
    [Serializable] internal sealed class DesktopSettings
    {
        public int schemaVersion = 2;
        public string pythonExecutable = "";
        public bool autoEnableGeneration = true;
    }
    [Serializable] internal sealed class DesktopJob
    {
        public string task, expectedVersion, prompt, contractId, output;
        public double? budget, maxMass;
        public string vehicle;
        public double? targetAltitude, cruiseSpeed, requiredDeltaV, minTwr, maxStallSpeed, minEndurance;
    }
    #pragma warning disable 0649 // Fields are populated by JsonUtility from the worker's shallow response.
    [Serializable] internal sealed class DesktopReply
    {
        public bool ok, apiReady, modelReady;
        public string task, pluginVersion, modelStatus, planFile, reportFile, assessment, error;
        public int partCount;
        public double wetMassTonnes, estimatedCost;
        public string[] missionSteps, assumptions;
        public string performanceStatus;
        public string[] performanceSummary;
    }
    #pragma warning restore 0649

    // All methods are invoked on Unity's main thread. The child only talks to the HTTP API.
    internal sealed class DesktopPanel : IDisposable
    {
        private readonly string dataFolder, gameRoot, clientFolder, settingsPath;
        private readonly Func<CraftPlan, PlanResult> build;
        private DesktopSettings settings = new DesktopSettings();
        private ClientProcess process;
        private string task, jobPath, expectedPlan, expectedReport, jobSave, jobFacility, jobContract;
        private bool initialized, wasReady, disposed, showSettings, showContracts, showReport;
        private bool canSaveSettings = true;
        private float nextContractRefresh;
        private ContractSummary[] contracts = new ContractSummary[0];
        private string selectedContract;
        private string prompt = "", budget = "", maxMass = "";
        private int vehicleIndex;
        private bool showPerformance;
        private string targetAltitude = "", cruiseSpeed = "", requiredDeltaV = "", minTwr = "", maxStallSpeed = "", minEndurance = "";
        private Vector2 scroll, contractScroll, reportScroll;
        private GUIStyle labelStyle;
        private DesktopReply lastReply;
        private string lastPlan, lastReport;
        private string lastSave, lastFacility, lastContract;
        internal string Status { get; private set; } = "等待编辑器就绪…";
        internal string ConnectionStatus { get; private set; } = "Python/API：尚未检查";
        internal bool Busy { get { return process != null && process.Running; } }
        internal bool AutoEnableGeneration { get { return settings.autoEnableGeneration; } }

        internal DesktopPanel(string dataFolder, string gameRoot, Func<CraftPlan, PlanResult> build)
        {
            this.dataFolder = dataFolder; this.gameRoot = gameRoot; this.build = build;
            clientFolder = Path.Combine(gameRoot, "GameData", "KSPAutoCraft", "Client");
            settingsPath = Path.Combine(dataFolder, "desktop.json");
            HubDependency.Refresh(gameRoot);
            try
            {
                if (File.Exists(settingsPath)) JsonUtility.FromJsonOverwrite(File.ReadAllText(settingsPath, Encoding.UTF8), settings);
                if (settings.schemaVersion != 1 && settings.schemaVersion != 2) throw new InvalidDataException("Unsupported settings version.");
                if (settings.schemaVersion == 1) { settings.schemaVersion = 2; SaveSettings(); }
            }
            catch
            {
                settings = new DesktopSettings { autoEnableGeneration = false };
                canSaveSettings = false; showSettings = true;
                Status = "desktop.json 无法读取，请检查配置文件；已有文件未覆盖。";
            }
        }

        internal void Tick(bool ready)
        {
            if (disposed) return;
            if (!ready && wasReady) Cancel();
            if (ready && !initialized)
            {
                initialized = true;
                RefreshContracts();
                if (canSaveSettings) Start("health");
            }
            wasReady = ready;
            if (ready && !Busy && Time.realtimeSinceStartup >= nextContractRefresh) RefreshContracts();
            if (process == null) return;
            var result = process.Poll();
            if (result == null) return;
            process.Dispose(); process = null;
            DeleteJobFile();
            if (result.state != "completed")
            {
                DesktopReply failure = null;
                try { failure = JsonUtility.FromJson<DesktopReply>(result.stdout); } catch { }
                Status = failure != null && !string.IsNullOrEmpty(failure.error) ? Plain(failure.error, 1500) :
                    "后台任务结束：" + result.state + ". " + Plain(result.stderr, 800);
                if (task == "health") ConnectionStatus = "Python/API：检查失败";
                return;
            }
            try
            {
                var reply = JsonUtility.FromJson<DesktopReply>(result.stdout);
                if (reply == null || !reply.ok || reply.task != task || reply.pluginVersion != ReleaseInfo.Version)
                    throw new InvalidDataException("后台返回了无效结果或不同的插件版本。");
                if (task == "health")
                {
                    ConnectionStatus = reply.apiReady ? "Python/API：已连接" : "Python/API：未就绪";
                    Status = Plain(reply.modelStatus, 1500);
                    return;
                }
                if (!ready || HighLogic.SaveFolder != jobSave || EditorDriver.editorFacility.ToString() != jobFacility)
                    throw new InvalidOperationException("存档或编辑器已变化，结果保留在磁盘但未生成候选。");
                if (!SamePath(reply.planFile, expectedPlan) || !SamePath(reply.reportFile, expectedReport) ||
                    !File.Exists(expectedPlan) || !File.Exists(expectedReport)) throw new InvalidDataException("后台返回的文件不属于当前任务。");
                if (reply.performanceStatus != "passed") throw new InvalidDataException("后台没有通过飞行性能筛选，未生成候选。");
                CheckContract(jobContract);
                lastPlan = expectedPlan; lastReport = expectedReport; lastReply = reply; showReport = true;
                lastSave = jobSave; lastFacility = jobFacility; lastContract = jobContract;
                GenerateCandidate();
            }
            catch (Exception error) { Status = Plain(error.Message, 1500); }
        }

        private void CheckContract(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return;
            Guid id;
            if (!Guid.TryParse(identifier, out id)) throw new InvalidDataException("无效合同编号。");
            var detail = ContractAdapter.Detail(id);
            if (detail == null || (detail.contract.state != "Active" && detail.contract.state != "Offered"))
                throw new InvalidOperationException("合同已结束或已不在当前存档中，请重新选择。");
        }

        private void GenerateCandidate()
        {
            if (string.IsNullOrEmpty(lastPlan)) return;
            if (HighLogic.SaveFolder != lastSave || EditorDriver.editorFacility.ToString() != lastFacility)
                throw new InvalidOperationException("设计不属于当前存档/编辑器。");
            CheckContract(lastContract);
            if (new FileInfo(lastPlan).Length > 256 * 1024) throw new InvalidDataException("设计文件超过大小限制。");
            var plan = PlanJson.Read(File.ReadAllText(lastPlan, Encoding.UTF8));
            var result = build(plan);
            Status = string.Format(CultureInfo.InvariantCulture, "候选已生成：{0} 部件，{1:F2} 吨，成本 {2:F0}。请检查报告后确认载入。",
                result.partCount, result.wetMassTonnes, result.estimatedCost);
        }

        private void RefreshContracts()
        {
            nextContractRefresh = Time.realtimeSinceStartup + 20;
            try
            {
                contracts = ContractAdapter.List("all", 0, 100).contracts.Where(c => c.state == "Active" || c.state == "Offered").ToArray();
                if (selectedContract != null && !contracts.Any(c => c.id == selectedContract)) selectedContract = null;
            }
            catch { contracts = new ContractSummary[0]; selectedContract = null; }
        }

        private void Start(string action)
        {
            if (Busy || disposed) return;
            try
            {
                if (!canSaveSettings) throw new InvalidOperationException("desktop.json 无法识别，请先修正配置文件。");
                if (!HubDependency.Refresh(gameRoot)) throw new InvalidOperationException(HubDependency.Status);
                if (!Path.IsPathRooted(settings.pythonExecutable) || !File.Exists(settings.pythonExecutable))
                    throw new InvalidOperationException("请在设置中填写 Python 可执行文件的完整路径，或运行 install-local.ps1 配置。 ");
                if (!File.Exists(Path.Combine(clientFolder, "ksp_autocraft", "__main__.py")))
                    throw new InvalidOperationException("缺少随插件安装的 Python 客户端，请使用 CKAN 修复安装。");
                var job = new DesktopJob { task = action, expectedVersion = ReleaseInfo.Version };
                if (action == "design")
                {
                    if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("请先输入设计需求。");
                    job.prompt = prompt;
                    job.contractId = selectedContract;
                    job.budget = Number(budget, "预算"); job.maxMass = Number(maxMass, "湿质量");
                    job.vehicle = new[] { "auto", "rocket", "aircraft" }[vehicleIndex];
                    job.targetAltitude = Number(targetAltitude, "目标高度"); job.cruiseSpeed = Number(cruiseSpeed, "巡航速度");
                    job.requiredDeltaV = Number(requiredDeltaV, "Δv 预算"); job.minTwr = Number(minTwr, "最低 TWR");
                    job.maxStallSpeed = Number(maxStallSpeed, "最大失速速度"); job.minEndurance = Number(minEndurance, "最低航时");
                    string folder = Path.Combine(dataFolder, "Designs"); Directory.CreateDirectory(folder);
                    expectedPlan = Path.Combine(folder, "design-" + Guid.NewGuid().ToString("N") + ".json");
                    expectedReport = Path.ChangeExtension(expectedPlan, ".report.json");
                    job.output = expectedPlan;
                    jobSave = HighLogic.SaveFolder; jobFacility = EditorDriver.editorFacility.ToString(); jobContract = selectedContract;
                }
                string jobs = Path.Combine(dataFolder, "Jobs"); Directory.CreateDirectory(jobs);
                jobPath = Path.Combine(jobs, "job-" + Guid.NewGuid().ToString("N") + ".json");
                using (var stream = new FileStream(jobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(ApiJson.Serialize(job));
                process = new ClientProcess();
                task = action;
                process.Start(settings.pythonExecutable, new[] { "-m", "ksp_autocraft", "--ksp-root", gameRoot, "desktop-worker", jobPath },
                    clientFolder, new Dictionary<string, string> { { "PYTHONPATH", clientFolder }, { "PYTHONIOENCODING", "utf-8" }, { "PYTHONUTF8", "1" } },
                    action == "health" ? 20000 : 2100000);
                Status = action == "health" ? "正在后台检查 Python、本地 API 与 AI Hub…" : "正在通过 AI Hub 设计并校验，完成后生成候选…";
            }
            catch (Exception error)
            {
                if (process != null) { process.Dispose(); process = null; }
                DeleteJobFile();
                Status = Plain(error.Message, 1500);
                if (action == "health") ConnectionStatus = "Python/API：需要配置";
                showSettings = true;
            }
        }

        internal bool Draw(bool allowGeneration)
        {
            if (labelStyle == null) labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(470));
            GUILayout.Label(ConnectionStatus, labelStyle);
            GUILayout.Label(HubDependency.Status, labelStyle);
            GUILayout.Label(Status, labelStyle);
            bool next = GUILayout.Toggle(allowGeneration, "允许生成候选文件（记住设置）");
            if (next != allowGeneration) { settings.autoEnableGeneration = next; SaveSettings(); allowGeneration = next; }
            if (Busy)
            {
                GUILayout.Label("后台任务进行中：" + process.ElapsedSeconds.ToString("F0", CultureInfo.InvariantCulture) + " 秒", labelStyle);
                if (GUILayout.Button("取消后台任务")) Cancel();
            }
            GUI.enabled = !Busy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("连接检查")) Start("health");
            if (GUILayout.Button("刷新合同")) RefreshContracts();
            if (GUILayout.Button(showSettings ? "隐藏设置" : "设置")) showSettings = !showSettings;
            GUILayout.EndHorizontal();
            if (showSettings) DrawSettings();
            var selected = contracts.FirstOrDefault(c => c.id == selectedContract);
            if (GUILayout.Button(selected == null ? "合同：自由设计（点击选择）" : "合同：" + Plain(selected.title, 110))) showContracts = !showContracts;
            if (showContracts)
            {
                contractScroll = GUILayout.BeginScrollView(contractScroll, GUILayout.Height(130));
                if (GUILayout.Button("不关联合同：自由设计")) { selectedContract = null; showContracts = false; }
                foreach (var contract in contracts)
                {
                    if (GUILayout.Button((contract.state == "Offered" ? "[待接] " : "[已接] ") + Plain(contract.title, 140)))
                    {
                        selectedContract = contract.id; showContracts = false;
                        if (string.IsNullOrWhiteSpace(prompt)) prompt = "为这个合同设计低成本、易操作的飞船，用中文解释分级和任务步骤。";
                    }
                }
                if (contracts.Length == 0) GUILayout.Label("当前没有可显示的合同；沙盒模式可使用自由设计。", labelStyle);
                GUILayout.EndScrollView();
            }
            GUILayout.Label("自然语言需求：", labelStyle);
            vehicleIndex = GUILayout.Toolbar(vehicleIndex, new[] { "自动识别", "火箭（VAB）", "飞机（SPH）" });
            GUI.SetNextControlName("KSPAutoCraft.prompt");
            prompt = GUILayout.TextArea(prompt, 4000, GUILayout.Height(85));
            GUILayout.BeginHorizontal();
            GUILayout.Label("预算", GUILayout.Width(42));
            GUI.SetNextControlName("KSPAutoCraft.budget"); budget = GUILayout.TextField(budget, 16, GUILayout.Width(115));
            GUILayout.Label("最大吨数", GUILayout.Width(65));
            GUI.SetNextControlName("KSPAutoCraft.mass"); maxMass = GUILayout.TextField(maxMass, 16, GUILayout.Width(100));
            GUILayout.EndHorizontal();
            if (GUILayout.Button(showPerformance ? "收起性能指标" : "性能指标（可选，留空使用任务默认）")) showPerformance = !showPerformance;
            if (showPerformance)
            {
                targetAltitude = TargetInput("目标高度 m", targetAltitude, "altitude");
                cruiseSpeed = TargetInput("巡航速度 m/s", cruiseSpeed, "speed");
                requiredDeltaV = TargetInput("火箭 Δv 预算 m/s", requiredDeltaV, "deltaV");
                minTwr = TargetInput("最低 TWR", minTwr, "twr");
                maxStallSpeed = TargetInput("最大失速速度 m/s", maxStallSpeed, "stall");
                minEndurance = TargetInput("最低满推力航时 s", minEndurance, "endurance");
            }
            GUILayout.Label("留空使用默认约束。点击设计才消耗模型额度；载入和发射仍需你操作。", labelStyle);
            GUI.enabled = !Busy && allowGeneration && HubDependency.Available;
            if (GUILayout.Button("设计并生成候选")) Start("design");
            GUI.enabled = !Busy && allowGeneration;
            if (lastPlan != null && GUILayout.Button("用上次方案重新生成候选"))
            {
                try { GenerateCandidate(); } catch (Exception error) { Status = Plain(error.Message, 1500); }
            }
            GUI.enabled = true;
            if (lastReply != null)
            {
                if (GUILayout.Button(showReport ? "收起任务报告" : "查看任务报告")) showReport = !showReport;
                if (showReport)
                {
                    GUILayout.Label("评估：" + lastReply.assessment + "（不代表合同已完成）", labelStyle);
                    foreach (var line in lastReply.performanceSummary ?? new string[0]) GUILayout.Label(Plain(line, 1000), labelStyle);
                    reportScroll = GUILayout.BeginScrollView(reportScroll, GUILayout.Height(170));
                    foreach (var line in lastReply.missionSteps ?? new string[0]) GUILayout.Label("• " + Plain(line, 1000), labelStyle);
                    GUILayout.Label("假设与待检查事项：", labelStyle);
                    foreach (var line in lastReply.assumptions ?? new string[0]) GUILayout.Label("• " + Plain(line, 1000), labelStyle);
                    GUILayout.Label("完整报告：" + lastReport, labelStyle);
                    GUILayout.EndScrollView();
                }
            }
            GUILayout.EndScrollView();
            return allowGeneration;
        }

        private void DrawSettings()
        {
            GUILayout.Label("Python 可执行文件完整路径：", labelStyle);
            GUI.SetNextControlName("KSPAutoCraft.python"); settings.pythonExecutable = GUILayout.TextField(settings.pythonExecutable ?? "", 2000);
            GUILayout.Label("模型入口：自动连接同一游戏目录中的 AI Hub。请在右上角 AI Hub 面板选择供应商、登录与模型。", labelStyle);
            GUILayout.Label("进入 VAB/SPH 时自动检查 AI Hub；仅在点击设计后调用模型。", labelStyle);
            if (GUILayout.Button("保存设置并检查")) { SaveSettings(); Start("health"); }
        }
        private string TargetInput(string label, string value, string id)
        {
            GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(210));
            GUI.SetNextControlName("KSPAutoCraft." + id); value = GUILayout.TextField(value, 20);
            GUILayout.EndHorizontal(); return value;
        }
        private void SaveSettings()
        {
            if (!canSaveSettings) { Status = "已有 desktop.json 无法识别，请先修正文件，避免覆盖。"; return; }
            string temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, ApiJson.Serialize(settings), new UTF8Encoding(false));
                if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null);
                else File.Move(temporary, settingsPath);
            }
            catch (Exception error) { Status = "设置未保存：" + Plain(error.Message, 600); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static double? Number(string text, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !PlanValidator.Finite(value) || value < 0)
                throw new InvalidOperationException(label + "必须是非负数字，使用小数点。");
            return value;
        }
        private static string Plain(string value, int limit)
        {
            value = Regex.Replace(value ?? "", "<[^>]*>", "");
            return value.Length <= limit ? value : value.Substring(0, limit) + "…";
        }
        private static bool SamePath(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        private void DeleteJobFile()
        {
            if (jobPath == null) return;
            try { File.Delete(jobPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            jobPath = null;
        }
        internal void Cancel()
        {
            if (process != null) { process.Cancel(); process.Dispose(); process = null; }
            DeleteJobFile(); Status = "后台任务已取消；已写出的方案文件会保留，未自动载入。";
        }
        public void Dispose() { if (!disposed) { Cancel(); disposed = true; } }
    }
}
