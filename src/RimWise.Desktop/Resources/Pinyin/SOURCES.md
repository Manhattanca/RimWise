# 拼音排序数据

以下为数据来源，未复制或运行上游程序代码；产品离线使用预生成的两个 TSV，正常构建无需下载或运行生成脚本。

| 上游仓库 | 作者／维护者 | 固定提交 | 源文件 SHA256 | 本项目处理 |
| --- | --- | --- | --- | --- |
| https://github.com/mozillazg/pinyin-data | mozillazg 及贡献者 | 9d05c9e29cb6a7e5c79f2f5279f5c1014246a19a | 90c11bd680ccfe7f2617a6e25abdb1fb87b5540df3e6f011ca50aaf3c7263a89 | pinyin.txt 0.15.0；每个 Unicode 码点保留首选读音，去声调，ü 写作 v |
| https://github.com/mozillazg/phrase-pinyin-data | mozillazg 及贡献者 | 195893902791321fe38ae7b4ebdd9905389cfd41 | 5eff64ffd4de072b7e7f96baec560c3366887f93e762cc35ef0a8f62a7259b73 | pinyin.txt 0.19.0；保留首选词读音，只保留与逐字首选读音不同的词语 |

生成脚本：`scripts/Update-PinyinData.ps1`。输入副本位于忽略提交的 `work/pinyin-source`，脚本先校验固定提交和源文件哈希。输出是 44,440 个字符读音和多音词例外，按最长词优先匹配；不根据上下文猜测所有多音字。补充平面汉字按 Unicode 码点读取，不使用 ANSI 编码。

两仓库以 MIT 许可证发布，原始许可分别保存在 `packaging/licenses/pinyin-data-LICENSE.txt`、`packaging/licenses/phrase-pinyin-data-LICENSE.txt`，随包分发。字符数据包含上游 Unihan 来源，另保留 `packaging/licenses/Unicode-LICENSE.txt`（Unicode, Inc.）。未知字符保留原文作为稳定比较键；同拼音以原名称、包名、安装路径消除歧义。
