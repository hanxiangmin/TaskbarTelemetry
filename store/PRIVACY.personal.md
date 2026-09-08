# TaskbarTelemetry 私人适配版隐私政策

生效日期：2026 年 9 月 8 日

TaskbarTelemetry 是一款在 Windows 任务栏显示本机系统状态和兼容额度状态的独立桌面工具。本政策适用于通过 Microsoft Store 分发、为现有电脑功能匹配而构建的版本。

## 1. 默认行为

- 第三方额度通知默认关闭，只有你在应用内阅读说明、勾选同意并保存后才启用。
- 开发者不会通过 TaskbarTelemetry 收集、接收、出售或建立你的个人数据档案。
- 应用不打开 Codex 的 `auth.json`，不读取或保存访问令牌，不安装驱动或 Windows 服务，也不修改 Windows 安全设置。
- 本版本包含未修改的 LibreHardwareMonitor 0.9.6 运行库，用于可选的 CPU 温度读取。应用本身不包含、下载或安装 PawnIO、驱动或驱动安装器；只有电脑上已经存在兼容且正常工作的温度访问组件时，CPU 温度才会显示。

## 2. 仅在本机处理的数据

为显示任务栏状态，应用会在本机读取和计算 CPU 占用与可用温度、内存、网络、可用的 NVIDIA GPU 指标，以及当前 Windows 用户近期兼容会话缓存中的额度快照字段（已用百分比、窗口时长和重置时间）。CPU 温度由随包提供的 LibreHardwareMonitor 运行库在本机读取；该温度值不会由 TaskbarTelemetry 上传。

本地额度模式最多检查最近 20 个 JSONL 会话文件，每个文件最多读取尾部 4 MiB。额度事件可能与其他事件共处于同一文件，因此文件尾部文本会被短暂读入内存并解析事件结构。应用只提取和保留额度数字与时间，不提取、展示、持久化或上传用户消息正文，也不打开 `auth.json`。本地缓存格式可能随相关产品更新而变化；没有兼容数据时，额度显示为不可用。

## 3. 可选的 Server酱通知

只有你在“通知与隐私设置”中明确同意后，应用才会通过 HTTPS 向第三方 **Server酱³**（`数字.push.ft07.com`）或 **Server酱 Turbo**（`sctapi.ftqq.com`），按你填写的密钥选择对应服务并发送：

- 已用和剩余百分比；
- 跨过的提醒档位；
- 额度窗口时长和重置时间。

这些数据由 Server酱转发到你自己配置的个人通知通道。应用不会在通知正文中发送会话正文、访问令牌、硬件温度或其他文件内容。SendKey 会通过 HTTPS 请求提交给 Server酱用于接口鉴权；它不会写入通知正文或应用日志。应用使用 Windows 当前用户数据保护（DPAPI）加密 SendKey 后再保存到本机。

Server酱对数据的处理、可用性和每日请求上限受其自己的条款与套餐约束。启用前请查看 [Server酱官方文档](https://sct.ftqq.com/docs/getting-started/sendkey/)。

## 4. 本地存储

应用可能保存：

- 可编辑的 `TaskbarTelemetry.ini` 配置；
- 通知同意标记；
- DPAPI 加密后的 SendKey；
- 通知档位、重试和每日请求计数状态。

Microsoft Store 版将这些文件保存在当前用户、当前应用包隔离的 `LocalState\TaskbarTelemetry` 目录中，不会回退读取传统版的配置、环境变量、同意记录、密钥或通知状态。首次运行从包内模板创建全新配置，通知保持关闭；传统版数据不会自动迁移。

传统未打包版使用 `%LOCALAPPDATA%\TaskbarTelemetry` 保存运行状态。旧版配置脚本可能曾使用固定名称 `SERVERCHAN_SENDKEY` 的用户环境变量；Microsoft Store 版不会读取或修改它。

## 5. 关闭、删除与卸载

选择“通知与隐私设置”→“停用并删除密钥”后，应用会先立即停止当前发送线程，再撤销本版本的同意，并删除本版本保存的 SendKey、通知主状态和临时状态；某项清理失败时会明确提示。

卸载 Microsoft Store 版会由 Windows 删除该应用包的隔离数据，但不会删除你以前单独运行的未打包版本数据、环境变量或电脑上独立安装的硬件访问组件。

## 6. 权限与第三方组件说明

TaskbarTelemetry 是 .NET Framework WinForms 桌面应用的 MSIX 包，需要 `runFullTrust`（普通用户 medium integrity）来创建任务栏覆盖窗口，并调用标准 Windows/.NET 桌面接口读取本机状态。应用不请求管理员权限，不注入或挂钩 Explorer，不安装 NT 服务、驱动或驱动安装器。

随包提供的 LibreHardwareMonitor 0.9.6 及四个支持程序集只用于本机 CPU 温度路径。若电脑没有兼容的温度访问组件，CPU 温度显示为不可用，其余功能继续工作。相关第三方许可见安装包内 `THIRD_PARTY_NOTICES.md`。

## 7. 独立性、联系与更新

TaskbarTelemetry 是独立第三方工具，与 OpenAI、LibreHardwareMonitor、PawnIO 及 Server酱运营方不存在隶属、合作或背书关系。支持联系方式由 Microsoft Store 产品页面提供。本政策如有实质变更，将随应用更新并修改生效日期；若新增第三方数据用途，应用会重新征求明确同意。
