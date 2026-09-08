# Microsoft Store 提交清单

## 需要本人完成

- 已在 Microsoft Store 开发者后台注册个人账号。
- 已完成身份证件审核；确认后台协议状态也显示完成。
- 在 Apps & games 中选择 New product → MSIX or PWA app，保留 `TaskbarTelemetry`（若不可用，先决定新名称）。
- 在 Product management → Product identity 复制 Identity Name、Publisher、Publisher display name。
- 填写真实支持联系人邮箱、IARC 年龄分级问卷，并在最后亲自点击 Submit for certification。

## 本地生成最终包

1. 把 `store/store-identity.example.json` 复制为 `store/store-identity.json`。
2. 原样粘贴 Partner Center 的三个身份字段。
3. 运行 `./build-store.ps1`。
4. 上传 `artifacts/store/TaskbarTelemetry_1.0.0.0_x64.msix`，不要上传 `_preview.msix`。

如仅供现有电脑使用且必须保留 CPU 温度，运行 `./build-store.ps1 -PersonalMatch -Version 1.0.1.0`，并上传 `artifacts/store/TaskbarTelemetry_1.0.1.0_x64_personal.msix`。此方案必须使用私人受众，并用 `.personal` 隐私、商店说明和认证说明文件替换普通商店材料。

## Partner Center 栏目

- Pricing and availability：Free、Public、所需市场、认证后尽快发布。
- Properties：Utilities & tools；填写或粘贴隐私政策；如实填写系统要求。
- Age ratings：按无暴力、无赌博、无用户生成内容的实用工具实际情况回答。
- Packages：上传最终 x64 MSIX，等待状态变为 Validated。
- Store listings：使用 `listing-zh-CN.md` 文案及 `listing-assets` 图片。
- Submission options：粘贴 `certification-notes.md`；在 restricted capability 处粘贴 runFullTrust 理由。
- Privacy：在 Properties 中如实选择会处理/发送的数据，优先粘贴 `PRIVACY.md` 正文；当前 MSIX 提交页也接受隐私政策文本。若以后有公开支持站点，可再改为 URL。

## 发布前核对

- 最终 manifest 的 Identity Name、Publisher 与 Partner Center 完全一致。
- 包版本为四段数字且第四段是 0。
- 包内没有 SendKey、证书私钥、驱动、服务安装器、LibreHardwareMonitor/PawnIO 后端或用户数据。
- 私人完全匹配版例外：允许且只允许 `lib` 下的 LibreHardwareMonitor 0.9.6 与四个指定支持 DLL；仍不得包含 PawnIO、驱动、安装器、SendKey、证书私钥或用户数据。
- 微信通知首次安装默认关闭，开机启动默认关闭。
- 截图来自实际应用界面，不使用营销海报，不包含桌面私人信息。
- 在最终 Product identity 包上运行 Windows App Certification Kit，并在干净的普通用户测试环境验证安装、启动、退出、卸载和开机启动入口。
- 所有栏目显示 Complete 后再提交认证。
