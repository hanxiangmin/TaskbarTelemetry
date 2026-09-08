# 参与开发

欢迎改进 TaskbarTelemetry。Issue 请附 Windows 版本、DPI、任务栏方向、显卡数量 / 型号、复现步骤和预期行为。截图请遮盖账户与无关桌面内容；不要上传 SendKey、OAuth 文件、完整会话日志或通知状态。

## 开发环境

Windows 10 / 11 x64，.NET Framework 4.8，PowerShell。生产代码兼容系统自带的 C# 编译器；请勿未经迁移直接引入新版本 C# 语法。

```powershell
.\build.ps1
.\test.ps1 -CompileOnly
.\test.ps1
```

`-CompileOnly` 只证明应用和测试代码可编译，不能记为测试通过。完整探针会读取本机硬件、做场景测试，并验证任务栏行为；测试通知在进程内关闭。若系统安全策略拦截执行，请报告阻止情况，勿关闭保护、改名或换宿主绕过。

## 找到要修改的地方

| 改什么 | 主要入口 |
|---|---|
| 单 / 双卡布局、字号、数值对齐 | `src/TaskbarRenderer.cs` |
| 任务栏嵌入、Explorer 恢复、悬停菜单 | `src/TaskbarForm.cs` |
| 显卡身份与排序 | `src/GpuTopology.cs`、`src/Collectors/NvmlCollector.cs` |
| CPU 名称与频率 | `src/CpuModelDetector.cs`、`src/CpuClockSelector.cs` |
| 额度来源 | `src/Collectors/`、`src/TelemetryEngine.cs` |
| 额度状态、过期、展示轮换 | `src/QuotaPresentation.cs` |
| Codex 通知 | `src/Notifications/` |
| 布局 / 来源场景测试 | `tests/AdaptiveLayoutTests.cs` |

## 新额度来源约定

1. 先确认供应商提供可授权、可只读查询的真实套餐额度来源，写明窗口口径。不能使用聊天 token 计数或 API 钱包余额代替套餐百分比。
2. 采集放在后台，返回按供应商区分的 `ProviderQuotaMetric`。原始响应中的账户标识、凭据、正文不得进入截图、日志或仓库。
3. 统一用剩余百分比；保留更新时间、窗口时长、重置时间。未连接、暂时失败和过期必须区分，不得把失败伪装成 `100%`。
4. 补齐零 / 一 / 两来源、掉线、恢复、重置、悬停暂停和旧数据过期测试。
5. 用真实返回值与对应账户页面核对；仅靠模拟值通过测试不得宣称真实接入完成。Kimi 当前仍处于这个验证关卡。
6. 新增通知渠道、后台服务或凭据保存必须单独评审并更新隐私说明，不默认启用。

## 界面与设备回归

保持单卡 / 双卡自适应，不硬编码维护者的硬件。测试 0 / 1 / 2 / 3 张 NVIDIA、传感器缺失、9% 与 100%、深浅主题及不同 DPI；单卡显示测试可以使用显示层筛选，但不能冒充单卡实体机验证。涉及窗口行为时还要人工测试托盘箭头、截图遮罩、Explorer 重启和 UAC。

重新生成 README 展示图：

```powershell
.\generate-readme-artwork.ps1
```

生成器直接使用生产绘制代码和固定演示数据，不启动采集器、不读账户、不发送通知。它不是诊断探针，也不执行测试断言。截图变化与代码变化应一起提交；不要用旧图或想象中的界面代表新版本。

## 提交与再分发

提交 PR 时说明动机、改动、验证结果和未验证项目。不要提交 `bin`、`lib`、`packages`、`test-artifacts`、`artifacts`、私人 Store 身份、运行配置副本、授权记录或密钥。默认配置的通知保持关闭。暂存待发布文件后，可运行 `./tools/Audit-Publish.ps1` 检查暂存内容；模式扫描不能替代人工复核。

Fork 和二次开发遵循根目录 MIT 许可，保留版权与许可证；第三方组件许可证独立适用。建议在衍生版 README 中链接上游并说明修改内容。未经维护者确认，不要将衍生版称为官方版。
