# Partner Center 填写指南

产品：TaskbarTelemetry

正式程序包：`artifacts/store/TaskbarTelemetry_1.0.0.0_x64.msix`

## 私人电脑完全匹配版

如果目标是仅在现有电脑上保留普通版的 CPU 温度与相同排版，请改用：

- 程序包：`artifacts/store/TaskbarTelemetry_1.0.1.0_x64_personal.msix`
- 受众：Private audience，只加入安装电脑所登录的 Microsoft 账号
- 隐私政策：`store/PRIVACY.personal.md`
- 商店说明：`store/listing-zh-CN.personal.md`
- 认证说明：`store/certification-notes.personal.md`
- 截图：`store/listing-assets/Screenshot-01-Taskbar.png` 和 `Screenshot-02-Privacy.png`

该包包含未修改的 LibreHardwareMonitor 0.9.6 运行库，但不包含、下载或安装 PawnIO、驱动或驱动安装器。CPU 温度仅在电脑上已经存在兼容且正常工作的温度访问组件时显示。必须在认证说明中如实披露；私人受众同样需要 Microsoft Store 认证。

## 1. Pricing and availability

- Price：Free
- Visibility / Audience：Public
- Markets：如无地区限制，可保留全部可用市场
- Release：建议选择通过认证后尽快发布；若希望先检查商店页，可选择手动发布时间
- 仅供本人使用时：选择 Private audience，并创建只包含本人 Microsoft 账号邮箱的已知用户组。首次发布为 Public audience 后不能再改回 Private audience。

## 2. Properties

- Category：Utilities & tools
- Privacy / Personal data：回答“是”。原因是用户明确启用后，应用会把额度百分比和重置时间发送给用户选择的第三方 Server酱服务。
- Privacy policy：粘贴仓库根目录 `PRIVACY.md` 的完整正文；如页面只接受 URL，则先把 `PRIVACY.html` 发布到公开 HTTPS 页面后再填写。
- System requirements：Windows Desktop x64，最低 Windows 10 2004（10.0.19041.0）
- Hardware：NVIDIA GPU 为可选；没有兼容 GPU 时其他功能仍可用，不要把它填成必需硬件
- Support contact：填写你能长期接收邮件的真实邮箱

## 3. Age ratings

按应用实际情况回答。此产品是本机系统监控工具：

- 暴力、性内容、赌博、毒品、恐怖内容：无
- 用户生成内容、公开聊天、社交网络：无
- 应用内购买、真钱交易：无
- 广告：无
- 精确位置：不使用
- 互联网通信：仅用户主动启用的 Server酱 HTTPS 通知，以及可选 app-server 模式

最终问卷和法律确认必须由账号持有人本人核对。

## 4. Packages

- 上传 `TaskbarTelemetry_1.0.0.0_x64.msix`
- 不要上传 `_preview.msix`
- 等待状态显示 Validated
- 设备族应显示 Windows Desktop / x64
- 包版本应显示 1.0.0.0

## 5. Store listings（zh-CN）

- Name：TaskbarTelemetry
- Short description、Description、Features、Keywords：从 `store/listing-zh-CN.md` 复制
- App icon：`store/listing-assets/AppTile300x300.png`
- Screenshot 1：`store/listing-assets/Screenshot-01-Taskbar.png`
- Screenshot 2：`store/listing-assets/Screenshot-02-Privacy.png`

截图由正式界面的生产绘制代码生成，使用演示数值，不包含用户桌面、真实额度或 SendKey。

## 6. Submission options / Notes for certification

粘贴 `store/certification-notes.md`。Restricted capability 的 `runFullTrust` 理由可填写：

> TaskbarTelemetry requires medium-integrity full trust to host its own non-activating WinForms taskbar child window, read local CPU, GPU, memory and network telemetry through standard Windows/.NET desktop APIs, maintain isolated per-user package settings, and send an optional HTTPS quota notification to a user-configured ServerChan endpoint. The app uses standard User32 `SetParent` to place its own window under `Shell_TrayWnd`; it does not request elevation, inject into or hook Explorer, install services or drivers, modify Windows security settings, or access other users' data.

## 7. 最后提交前

- 每个栏目都显示 Complete
- Packages 显示 Validated
- 隐私政策、支持邮箱、截图均已填写
- 检查没有误上传 preview 包
- 最后的 Submit for certification 由账号持有人本人点击
