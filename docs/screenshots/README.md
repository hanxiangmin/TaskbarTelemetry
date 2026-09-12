# README 展示图

这些 PNG 由 `tools/ReadmeArtwork.cs` 调用生产 `TaskbarRenderer` 绘制。

- `hero.png`：README 首屏海报。
- `layout-overview.png`：右侧对齐的单 / 双卡布局对照。
- `single-dark.png`、`dual-dark.png`：深色界面。
- `single-light.png`、`dual-light.png`：浅色界面。

所有硬件数字与 Codex 额度均为固定演示值；CPU 标签是演示配置，不从维护者个人硬件导出。未接入 Kimi 数值。图片没有真实桌面、用户账号、密钥或通知状态。条形界面按 144 DPI、8.5 pt 和 `scaleWidthWithDpi=false` 的默认紧凑设置绘制，宽度为 520 / 476 像素；海报和对照图将整个条形界面等比例放大 2×，不单独拉宽列间距。

2026-09-12 更新：图片与 v1.0.1 下载包使用相同的生产绘制代码，展示 520 / 476 布局及 `0.16 Mb` / `12.16 Mb` 网速格式。v1.0.0 的历史展示图保留在该版本标签中。

在项目根目录执行 `./generate-readme-artwork.ps1` 可重新生成。该工具不启动采集、联网或发送通知，也不执行诊断探针或测试断言。生成图片成功不代表真实 Windows 桌面兼容性测试通过。
