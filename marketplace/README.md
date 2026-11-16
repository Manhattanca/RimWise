# 排序市场

本目录保存公开排序资料，不保存模组本体、个人路径、存档或登录凭据。

正式数据位置已拆分至公开仓库 `Manhattanca/RimWise-Marketplace`，本目录保留开发格式参考，不再作为客户端的线上上游。独立仓库部署源在 `development/marketplace-repository`。

客户端匿名读取 `index.json`；选择条目只浏览目录元数据，点击下载后才获取对应清单，按目录中的 SHA-256 验证原始文件。初始目录为空，不随软件内置全部排序。

作者使用 GitHub 登录，在 RimWise 中提交新版本。每个提交申请只新增一份清单和其中声明的最多三张图片：

`marketplace/manifests/<github-login>/<collection-id>/<version>.json`

作者名必须与提交账号相同。已上架版本不可修改；更新请增加版本号。简介等资料也随版本保存。维护者审核后合并，工作流重新生成目录。

示例结构（仅格式说明，不是可用合集）：

```json
{
  "schemaVersion": 1,
  "id": "my-collection",
  "version": "1.0.0",
  "title": "我的合集",
  "author": "YourGitHubLogin",
  "description": "合集说明、测试条件及已知问题。",
  "gameVersion": "1.7",
  "requiredDlc": [],
  "workshopCollectionId": null,
  "mods": [
    { "packageId": "ludeon.rimworld", "workshopId": null }
  ]
}
```

`mods` 数组顺序就是加载顺序。工坊模组必须附带工坊编号，本体和 DLC 不填写工坊编号。允许清单声明的 PNG/JPEG 预览图，不分发本地模组或任意文件包。所需 DLC 使用实际 packageId，并须包含在排序中。

本地维护命令：

```powershell
dotnet run --project tools/RimWise.MarketplaceCatalog -- build .
```

首次部署时启用仓库 Actions，并检查分支保护允许维护者采用的目录更新方式。PR 校验只编译基准分支的可信工具，把作者提交作为 JSON 数据读取，不执行作者代码。新作者的 Actions 运行可能需要维护者批准；校验通过仍需人工审核，不代表实机兼容。

软件发布登录采用 OAuth 授权码与 PKCE、动态 loopback 回调。公开 Client ID 随软件提供；受控测试构建通过进程环境注入维护者提供的共享 Client Secret，不存入仓库。客户端共享 Secret 可被提取，不宣称保密；用户 access token 使用当前用户 DPAPI 保护。下载不需要 GitHub 账号。
