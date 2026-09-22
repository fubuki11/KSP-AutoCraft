# CKAN 本地安装与 ZIP 导入问题

## 推荐的本地安装入口

先安装 AI Hub 依赖，保存并退出 KSP，正常关闭 CKAN。在仓库根目录构建并执行：

```powershell
$ksp = 'D:\steam\steamapps\common\Kerbal Space Program'
.\scripts\build.ps1 -KspRoot $ksp
.\scripts\install-local.ps1 -KspRoot $ksp -Upgrade
```

`-Upgrade` 允许升级已登记的旧版本；首次安装也可使用该入口。安装器会注册/刷新本项目的本地仓库，安装明确版本，再验证注册表、文件归属和实际内容。

保留 `dist` 中的安装 ZIP 和 `KSPAutoCraft-local-repository.zip`，后者是索引而不是 Mod 安装包。移动源码目录后应重新构建，并确认 CKAN 的本地仓库地址。

## 导入 ZIP 后列表没有插件

CKAN 1.36.4 的 **Import downloaded mods…** 可能只把 ZIP 放进缓存。对于尚未被仓库索引收录的 Mod，GUI 不一定创建实际安装任务。

请使用上述脚本，并在 CKAN 的 **Installed / 已安装** 筛选中确认 `KSPAutoCraft`。缓存中存在 ZIP 或命令退出码为 0，都不能单独证明安装完整。

参考：[MainImport.cs](https://github.com/KSP-CKAN/CKAN/blob/v1.36.4/GUI/Main/MainImport.cs)、[Install.cs](https://github.com/KSP-CKAN/CKAN/blob/v1.36.4/Cmdline/Action/Install.cs)。

## 升级未收录版本时误报“已是最新”

CKAN 1.36.4 的 `upgrade --ckanfile` 只取文件中的 Mod ID，版本仍从仓库查找。仅提供新的 `.ckan` 不能保证升级到它的版本。

安装器会注册/刷新 `KSPAutoCraft-local`，然后执行 `upgrade KSPAutoCraft=<明确版本>`。它不会强制降级，不会直接修改 `registry.json`，也不会覆盖指向其他位置的同名仓库。

## ZIP 占用与安装锁

CKAN 1.36.4 某些 ZIP 导入路径没有及时释放 `ZipFile`，随后删除/移动源文件可能触发 sharing violation。导入缓存时选择保留源文件，或退出 CKAN 后使用显式安装脚本。

- 不要删除正在使用的 `registry.locked`；正常退出 CKAN，包括托盘中的实例。
- 报错中的“另一个进程”也可能是 CKAN 自己尚未释放的 ZIP 句柄。
- 构建脚本会在替换发布文件前检查占用，失败时保留已有包。

参考：[ModuleImporter.cs](https://github.com/KSP-CKAN/CKAN/blob/v1.36.4/Core/IO/ModuleImporter.cs)、[NetFileCache.cs](https://github.com/KSP-CKAN/CKAN/blob/v1.36.4/Core/Net/NetFileCache.cs)。

## 只读诊断

```powershell
.\scripts\diagnose-import.ps1 -Archive '.\dist\KSPAutoCraft-0.6.1.zip'
```

如果操作的是其他位置的 ZIP，填写实际路径。工具检查共享/独占读取、注册表和 Windows Restart Manager 的持有进程信息，不结束进程、不删除锁文件。

需要复现 CKAN 库的句柄行为时，可设置 `CKAN_LIB_DIRECTORY` 指向实际 CKAN 程序集目录，然后运行 `tests/KSPAutoCraft.CkanImportTests`。发布包检查通过 `python -m unittest discover -s tests/python -p test_package.py -v` 执行。
