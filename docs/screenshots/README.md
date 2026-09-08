# README 展示图

这些 PNG 由 `tools/ReadmeArtwork.cs` 调用生产 `TaskbarRenderer` 绘制。

- `hero.png`：README 首屏海报。
- `layout-overview.png`：右侧对齐的单 / 双卡布局对照。
- `single-dark.png`、`dual-dark.png`：深色界面。
- `single-light.png`、`dual-light.png`：浅色界面。

所有硬件数字与 Codex 额度均为固定演示值；CPU 标签是演示配置，不从维护者个人硬件导出。未接入 Kimi 数值。图片没有真实桌面、用户账号、密钥或通知状态。条形界面为 192 DPI / 2× 绘制，保存为 PNG；海报只增加背景和说明文字。

在项目根目录执行 `./generate-readme-artwork.ps1` 可重新生成。该工具不启动采集、联网或发送通知，也不执行诊断探针或测试断言。生成图片成功不代表真实 Windows 桌面兼容性测试通过。
