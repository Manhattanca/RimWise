# 第三方来源登记

压缩存档读取：`oleg-st/ZstdSharp`（Oleg Stepanischev，https://github.com/oleg-st/ZstdSharp），ZstdSharp.Port 0.8.8，固定提交 `2cd0c019693bc786a5fe5c3be94e107b24e7267e`，MIT。使用其纯托管流式解压器识别 Zstandard 存档并提取模组元数据，无需额外安装解压工具。随包分发 `ZstdSharp.dll`，许可见 `packaging/licenses/ZstdSharp-LICENSE.txt`；同时保留其所基于的 Zstandard 1.5.7（Facebook, Inc.，https://github.com/facebook/zstd）的 BSD 许可 `packaging/licenses/zstd-LICENSE.txt`。

仅登记实际采用的第三方代码、运行依赖与数据，以及相应许可证和声明。仅查阅文档、研究方法或核对格式且未采用实现的项目不列入公开登记。

下列许可证文件位于 packaging/licenses；发布包内对应目录为 licenses。第三方许可独立于本项目许可证。

## 第三方组件与数据来源

`chromium/chromium`（The Chromium Authors）：受限进程有效访问检查改编自 Chromium 的 CheckLpacToken 测试设计；保留 BSD 三条款许可，见 `packaging/licenses/chromium-LICENSE.txt`。

| GitHub 仓库 | 开发者／维护组织 | 用途及版本 | 来源 |
| --- | --- | --- | --- |
| `dotnet/runtime` | .NET Foundation、Microsoft 及贡献者 | .NET 框架与文件、XML API；目标 .NET 10，当前在 10.0.1 验证 | https://github.com/dotnet/runtime |
| `dotnet/wpf` | .NET Foundation、Microsoft 及贡献者 | WPF Windows 桌面界面；当前 WindowsDesktop 10.0.1 验证 | https://github.com/dotnet/wpf |

本包依赖电脑已有的完整运行时，没有复制框架源码；EXE 使用 .NET 生成的原生启动器（apphost）。已附 `../packaging/licenses/dotnet-runtime-LICENSE.txt`，以及当前 SDK 安装随附的完整第三方声明 `../packaging/licenses/dotnet-THIRD-PARTY-NOTICES.txt`；完整声明包含框架可能使用的组件，不代表本软件单独引入了每一项。WPF 的许可与声明也可在上述仓库查阅。后续自包含发布会重新核对实际分发组件。

本版本使用以下两份数据库的派生索引，未复制 RimSort 应用源码。RimWise 为独立项目，与 RimSort 项目没有关联。

社区数据库：GitHub 仓库 `RimSort/Community-Rules-Database`（https://github.com/RimSort/Community-Rules-Database）和 `RimSort/Steam-Workshop-Database`（https://github.com/RimSort/Steam-Workshop-Database），开发者／维护者为 **RimSort 维护团队及贡献者**。内置来源提交分别为 `ab470f6cc318629519c873f8820adf28d4a24660` 和 `6180a5e4e6c3ddf87f281fd8f1a020203e9a30b3`，提取 631 个社区规则项和 18,645 个带包名工坊条目，保留必要前置关系，以约 434 KiB 压缩索引嵌入；原始数据库不随包分发。两库均为 CC0-1.0，原许可分别随包保留于 `../packaging/licenses/RimSort-Community-Rules-Database-LICENSE.txt` 和 `../packaging/licenses/RimSort-Steam-Workshop-Database-LICENSE.txt`。用户可手动更新至新的固定提交快照，已有任务仍使用创建时快照；数据库没有逐游戏版本规则保证，不能替代游戏测试。

离线拼音数据：GitHub 仓库 `mozillazg/pinyin-data`（https://github.com/mozillazg/pinyin-data）及 `mozillazg/phrase-pinyin-data`（https://github.com/mozillazg/phrase-pinyin-data），作者／维护者为 **mozillazg 及贡献者**。固定提交分别为 `9d05c9e29cb6a7e5c79f2f5279f5c1014246a19a` 和 `195893902791321fe38ae7b4ebdd9905389cfd41`。提取首选读音、去声调与多音词例外后嵌入软件，不加载或执行上游程序代码，使用时无需联网。MIT 许可随包附于 `../packaging/licenses/pinyin-data-LICENSE.txt` 和 `../packaging/licenses/phrase-pinyin-data-LICENSE.txt`；字符数据的上游 Unihan 数据另保留 Unicode, Inc. 的 `../packaging/licenses/Unicode-LICENSE.txt`。

固定测试辅助 Mod 为本项目自行编写，嵌入集成组件，测试时才释放；不分发游戏 DLL 或编译占位程序集。SteamBridge 随包分发 GitHub 仓库 `rlabrecque/Steamworks.NET`（开发者 **Riley Labrecque 及贡献者**，https://github.com/rlabrecque/Steamworks.NET）官方 2025.164.1 版本的未修改 Windows x64 托管库，MIT 许可见 `../packaging/licenses/Steamworks.NET-LICENSE.txt`。另含 Valve Corporation 的专有原生 `steam_api64.dll`（SDK 1.64），不受 MIT 或本项目开源许可证覆盖；来源、SHA256 和权属见 `../packaging/licenses/Valve-Steamworks-NOTICE.txt`。需要 Steam 已登录且账户拥有 RimWorld；软件不分发或替换游戏的 Steam DLL，不包含 steam_appid.txt。


## 本地反编译组件来源

`pardeike/DecompilerServer`（Andreas Pardeike，https://github.com/pardeike/DecompilerServer），MIT，固定提交 `42c3f9106c20e5597cd11bcab70c5937d101f03e`。采用并修改 OwnedAssemblyResolver 程序集解析器，保留来源与 MIT 声明。RimWise 自身服务仅允许会话白名单程序集及指定方法，增加路径与散列校验，禁用 PDB／SourceLink／任意路径查询；未集成上游通用工具。许可见 `../packaging/licenses/DecompilerServer-LICENSE.txt`。

`icsharpcode/ILSpy`（ILSpy Team、AlphaSierraPapa 及贡献者，https://github.com/icsharpcode/ILSpy），ICSharpCode.Decompiler 10.0.1.8346，MIT。原许可及声明见 `../packaging/licenses/ILSpy-MIT-LICENSE.txt`、`../packaging/licenses/ILSpy-LICENSE.txt` 和 `../packaging/licenses/ILSpy-THIRD-PARTY-NOTICES.txt`。

`modelcontextprotocol/csharp-sdk`（Model Context Protocol C# SDK 贡献者，https://github.com/modelcontextprotocol/csharp-sdk），ModelContextProtocol 1.3.0；保留上游包含 Apache-2.0 转换及既有 MIT 条款的完整许可，见 `../packaging/licenses/ModelContextProtocol-LICENSE.txt`；上游完整第三方声明见 `../packaging/licenses/ModelContextProtocol-THIRD-PARTY-NOTICES.txt`。

`dotnet/runtime`（.NET Foundation、Microsoft 及贡献者，https://github.com/dotnet/runtime），Microsoft.Extensions.Hosting 10.0.8 及其运行依赖，MIT；许可证与依赖声明见 `../packaging/licenses/dotnet-Extensions-LICENSE.txt` 和 `../packaging/licenses/dotnet-Extensions-THIRD-PARTY-NOTICES.txt`。这些组件随 `Decompiler` 文件夹分发。


`dotnet/extensions`（.NET Foundation、Microsoft 及贡献者，https://github.com/dotnet/extensions），Microsoft.Extensions.AI.Abstractions 10.5.2，作为 MCP SDK 的传递依赖使用，MIT。许可见 `../packaging/licenses/dotnet-AI-Abstractions-LICENSE.txt`，完整上游声明见 `../packaging/licenses/dotnet-AI-Abstractions-THIRD-PARTY-NOTICES.txt`。
## 安装包组件

EXE 安装包使用 Inno Setup 6.7.4（Jordan Russell、Martijn Laan，https://jrsoftware.org/），许可见 `licenses/Inno-Setup-LICENSE.txt`。简体中文消息采用 jrsoftware/issrc 随附的 ChineseSimplified.isl，维护者 Zhenghan Yang (Kira)，保留文件内署名。安装界面仅使用简体中文。

EXE 随包提供 Microsoft .NET 10.0.14 Desktop Runtime x64 官方原版安装程序，仅在缺少兼容运行时时安装；微软组件保留其原有许可和署名。ZIP 不含运行时，需要电脑已安装 .NET 10 Desktop Runtime x64。
