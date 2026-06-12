# VSCN Vintage Story 汉化包

这是一个面向 Vintage Story 模组汉化的聚合语言包仓库。

仓库中的翻译资源按模组名、目标模组版本和真实 `modid` 组织，最终由本地打包器生成一个可直接放入 Vintage Story `Mods` 目录的聚合包 zip。首版只提供目录结构、打包工具、测试和贡献说明，不包含任何真实模组翻译样例。

## 环境要求

- .NET SDK 8 或更高

## 仓库结构

```text
config/packer/default.json
projects/assets/<mod-name>/<mod-version>/<modid>/lang/zh-cn.json
src/Packer
tests/Packer.Tests
```

其中：

- `<mod-name>` 仅用于仓库内组织，不进入最终产物。
- `<mod-version>` 表示目标模组版本，不表示游戏版本。
- `<modid>` 必须使用被汉化模组的真实 `modid`。

可选地，你也可以在同目录保留源语言文件，例如：

```text
projects/assets/<mod-name>/<mod-version>/<modid>/lang/en.json
```

打包时只会输出 `zh-cn.json`。

## 新增翻译

1. 在 `projects/assets` 下创建目录：

   ```text
   projects/assets/<mod-name>/<mod-version>/<modid>/lang/
   ```

2. 放入简体中文语言文件：

   ```text
   projects/assets/<mod-name>/<mod-version>/<modid>/lang/zh-cn.json
   ```

3. 保证 `zh-cn.json` 是合法 JSON，且根节点是对象。

当同一个真实 `modid` 同时存在多个目标模组版本时，打包器会默认选择最高版本；如果版本无法比较、同一归一化版本重复，或最终输出路径发生冲突，打包会直接失败并列出冲突来源。

## 本地打包

在仓库根目录运行：

```powershell
dotnet run --project src/Packer -- pack --config config/packer/default.json
```

默认输出文件：

```text
build/VSCN-VintageStory-Chinese-Language-Pack-<version>.zip
```

产物根目录固定为：

```text
modinfo.json
assets/<真实modid>/lang/zh-cn.json
```

## 运行测试

```powershell
dotnet test
```

测试会覆盖路径扫描、版本选择、冲突处理、JSON 校验、zip 输出结构以及 CLI 参数契约。

## GitHub Actions

当 Pull Request 包含翻译文件或打包相关变更时，GitHub Actions 会自动执行一次打包，并把生成的 zip 作为 artifact 上传。

PR 产物使用 UTC 时间戳版本号，并保持为 SemVer 兼容格式，示例：

```text
2026.6.12-pr083830
```

因此 `modinfo.json` 里的 `version` 字段，以及输出文件名中的 `{version}`，都会随每次 PR 打包变化。

## 安装到 Vintage Story

1. 运行打包命令生成 zip。
2. 将生成的 zip 放入 Vintage Story 的 `Mods` 目录。
3. 启动游戏后，语言包会以 `vscnlangpack` 这个模组 ID 被识别，并覆盖你已安装且受支持模组的对应简体中文语言文件。
