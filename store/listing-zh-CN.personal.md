# TaskbarTelemetry 私人适配版商店详情（简体中文）

## 说明

TaskbarTelemetry 把常看的本机状态放在系统通知区域左侧，以紧凑的双行布局持续显示，无需打开单独的监控窗口。

你可以一眼查看实时上传/下载速度、CPU 占用与可用温度、内存占用、NVIDIA GPU 显存、占用率和温度，以及兼容的 Codex 本地会话缓存中最近一次主额度窗口剩余比例。数值采用固定槽位排版，个位数、两位数和 100 之间切换时不会带动整行跳动。没有兼容 GPU、温度组件或额度数据时，相应位置会显示不可用，其余功能照常工作。

可选的个人通知会在额度每跨过一个设定档位时，通过你自己的 Server酱³ 或 Turbo 账户发送（³ 使用独立 App 接收，Turbo 使用微信等已配置通道）。该功能默认关闭；只有你在应用内查看数据用途、明确同意并提供自己的 SendKey 后才会启用。你可以随时立即停用通知并删除密钥。实际提醒数量受 Server酱可用性、每日请求上限和用户套餐约束；应用默认本地每天最多尝试 5 次，可按账户套餐调整。

隐私设计：本地模式只保留额度数字和时间；不打开 auth.json，不读取或保存访问令牌，也不提取、持久化或上传用户消息正文。SendKey 由 Windows 当前用户数据保护加密保存，仅通过 HTTPS 提交给 Server酱用于接口鉴权。

本版本为保持现有电脑上的 CPU 温度显示，随包提供未修改的 LibreHardwareMonitor 0.9.6 运行库。应用不包含、下载或安装 PawnIO、驱动、驱动安装器或 Windows 服务；CPU 温度只在电脑上已经存在兼容且正常工作的温度访问组件时显示。应用不请求管理员权限，也不修改 Windows 安全设置。

TaskbarTelemetry 是独立第三方工具，与 OpenAI、LibreHardwareMonitor、PawnIO 及 Server酱运营方不存在隶属、合作或背书关系。

## 产品功能

1. 任务栏双行实时显示系统状态
2. 查看网速、CPU 占用与可用温度、内存和 NVIDIA GPU 指标
3. 显示兼容额度的剩余比例与重置时间
4. 支持通过个人 Server酱发送额度档位提醒
5. 通知默认关闭，SendKey 加密保存在本机
6. 不读取访问令牌，不上传消息正文
7. 不请求管理员提权，不安装驱动或系统服务

## 关键词

- 任务栏监控
- 系统状态
- 网速监控
- CPU 温度
- GPU 监控
- 额度监控
- 通知提醒

## 图片

- 300×300 图标：`store/listing-assets/AppTile300x300.png`
- 实际界面截图：`store/listing-assets/Screenshot-01-Taskbar.png`
- 隐私设置截图：`store/listing-assets/Screenshot-02-Privacy.png`
