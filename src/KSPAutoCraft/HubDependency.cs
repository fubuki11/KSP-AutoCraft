using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace KSPAutoCraft
{
    internal static class HubDependency
    {
        internal static bool Available { get; private set; }
        internal static string Status { get; private set; } = "AI Hub：尚未检查";

        internal static bool Refresh(string gameRoot)
        {
            Available = false;
            string dll = Path.Combine(gameRoot, "GameData", "KSPAIHub", "Plugins", "KSPAIHub.dll");
            if (!File.Exists(dll))
                Status = "AI Hub：未安装。请通过 CKAN 安装 KSPAIHub 0.4.0 或更新版本，然后重启 KSP。";
            else
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "KSPAIHub");
                if (assembly == null)
                    Status = "AI Hub：文件存在，但插件未加载。请检查安装并重启 KSP。";
                else if (assembly.GetName().Version < new Version(0, 4, 0, 0))
                    Status = "AI Hub：版本过旧。请升级到 0.4.0 或更新版本并重启 KSP。";
                else
                {
                    Available = true;
                    Status = "AI Hub：已加载 " + assembly.GetName().Version + "；模型与登录由 AI Hub 管理。";
                }
            }
            return Available;
        }
    }

    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class HubStartupCheck : MonoBehaviour
    {
        private void Start()
        {
            HubDependency.Refresh(KSPUtil.ApplicationRootPath);
            if (HubDependency.Available) Debug.Log("[KSPAutoCraft] " + HubDependency.Status);
            else Debug.LogWarning("[KSPAutoCraft] " + HubDependency.Status);
        }
    }
}
