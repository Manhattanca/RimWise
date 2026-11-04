此项目为全AI项目，无真人含量，不推荐任何人提交修改。如有需要请直接提供Issues

# RimWise

一个仍在测试中的 RimWorld 模组管理与自动排序工具。提供传统规则排序，也可以连接自己的 AI 服务，通过启动游戏、采集日志、调整顺序和重新测试来排查问题。

## 功能

- 模组搜索、批量启用和移出、拖动排序、颜色标记、撤销与恢复。
- 导入排序与存档；结合元数据、社区规则和依赖关系补齐前置、调整顺序。
- 为新增 Mod 寻找位置，或重新调整完整排序；遇到移出候选按用户的确认设置处理。
- 经 Steam 启动 Steam 版游戏，通过临时辅助 Mod 检查新游戏流程和日志。
- 本地静态分析与受限反编译，为 AI 提供相关证据；不执行被分析的程序集。
- 记录实际回报的 token 用量；未回报用量单独标记。支持导出脱敏诊断日志。
- 后台检查 GitHub Releases 更新，校验下载后通知用户；由用户决定何时安装。

日常模组管理和规则排序无需 AI。AI 自动排障需要用户自行配置服务，服务费用由对应提供商收取。AI 接收筛选、脱敏后的技术信息，不能任意读取电脑文件或执行命令。

## 构建与运行

需要 Windows x64、.NET SDK **10.0.101**（兼容补丁版本可用）、.NET 10 Desktop Runtime 和 PowerShell。固定辅助 Mod 的编译使用 Windows 自带的 .NET Framework 4.x 参考程序集，不需要提供游戏 DLL。

在源码目录执行：

```powershell
.\scripts\Build-Source.ps1
```

如需指定下载缓存：

```powershell
.\scripts\Build-Source.ps1 -PackageCacheRoot 'E:\RimWise-dependencies'
```

默认缓存在源码目录的 `work/nuget` 下。构建脚本从 NuGet 恢复依赖，并从 Steamworks.NET 官方发布下载两个固定版本的桥接依赖，校验 SHA256 后使用。源码仓库不包含依赖 DLL。

脚本编译 Release 并执行离线回归，不调用付费 AI，也不启动 Steam 或游戏。输出在 `work/bin/RimWise.Desktop/Release/net10.0-windows/`；运行其中的 `RimWise.exe`，保留同目录的 `SteamBridge`、`Decompiler` 和其他运行文件。

## 使用与更新

### 构建分发包

运行 `scripts/Publish-Preview.ps1 -Version 0.5.1` 可生成 ZIP。制作中文安装包时，另行安装 Inno Setup 6.7.4，并从微软官网下载 .NET Desktop Runtime 10.0.14 x64 安装程序，再运行 `scripts/Build-Installer.ps1 -Version 0.5.1 -Compiler <ISCC.exe路径> -RuntimeInstaller <运行时安装程序路径>`。脚本核对运行时散列和微软签名；中文消息和安装脚本随源码提供。分发目录位于源码目录的同级 `releases` 中。

启动后核对游戏、模组和配置路径，再导入或调整排序。AI 功能在“自动排序”窗口配置。用户数据独立于程序目录，密钥在本机加密保存；迁移数据请使用软件的路径设置。

程序更新仅使用本仓库的 [GitHub Releases](https://github.com/Manhattanca/RimWise/releases)。有安装包时优先提供安装包；ZIP 版本请关闭软件，清空原程序目录后再解压，保留独立的用户数据目录。没有发布附件时会提示暂时无法取得更新，不影响使用。

当前仓库公开源码；可下载的程序版本以 Releases 实际附件为准。后台更新已经验证过正式发布附件的下载与校验；安装包同时支持安装和更新。

## 问题反馈

请提交 [Issues](https://github.com/Manhattanca/RimWise/issues)，说明软件版本、游戏版本、操作步骤、预期与实际结果，并附软件导出的诊断日志。上传前检查日志中的私人内容；不要发送 API 密钥、账号密码或完整用户数据目录。

维护者优先接受问题反馈，不鼓励提交代码修改。这是协作偏好，不改变 MIT 许可证授予的权利。

## 许可证与致谢

RimWise 自有代码使用 [MIT](LICENSE)。实际采用的第三方代码、库与数据保留各自许可；来源、版本和用途见 [第三方来源](docs/THIRD_PARTY.md)，许可原文位于 [packaging/licenses](packaging/licenses)。

感谢 .NET/WPF、Steamworks.NET、ILSpy、DecompilerServer、Model Context Protocol C# SDK、Chromium、RimSort 社区数据库和拼音数据的维护者与贡献者。Valve 的原生 Steamworks 组件为专有组件，不属于 RimWise 的 MIT 授权范围。

本项目为独立社区工具，与 Ludeon Studios、Valve、RimSort 无隶属关系。RimWorld、Steam 及其他第三方名称与商标属于其各自权利人。
