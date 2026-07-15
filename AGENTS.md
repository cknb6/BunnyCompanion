# 小申陪伴（BunnyCompanion）— 项目交接

> **本软件由传康KK开发。**

面向 **Windows 10/11** 的 WPF 桌面宠物 + 本机 Agent。macOS 上只改代码/交叉编译，**不能**在本机跑 WPF EXE。

## 产品

| 项 | 值 |
|----|-----|
| 产品名 | 小申陪伴 |
| 工程名 | BunnyCompanion |
| **开发者** | **传康KK**（传康Kk / 万能程序员） |
| 技术 | C# · WPF · .NET 8 · 自包含单文件 EXE |
| 默认桌宠名 | 小申 |
| 默认用户称呼 | **宝宝**（历史「宝贝」在 Normalize 时会迁到「宝宝」） |
| 程序内声明 | `Services/AppCredits.cs` · 托盘「关于」展示 |

## 作者与联系（勿混用邮箱）

| 场景 | 内容 |
|------|------|
| 产品署名（用户可见） | **本软件由传康KK开发** |
| GitHub / PR / push 提交 | `user.name`：传康KK 或 `传康Kk`；`user.email`：**1837620622@qq.com**（账号 **1837620622**） |
| 文档/交付/用户向联系 | 微信：1837620622 · 邮箱：**2040168455@qq.com** · 咸鱼/B站：万能程序员 |
| 禁止 | 把 `2040168455@qq.com` 写进 GitHub commit author；论文署名不要改成 GitHub 邮箱 |

## 仓库与发布（双账号分工）

| 账号 | 角色 | 说明 |
|------|------|------|
| **1837620622**（大号 · 传康Kk） | 开发者身份 / 协作 / 提交署名 | Actions **额度不足**时不要用此号跑 CI 打包 |
| **cknb6**（小号） | **仓库 owner + Actions 打包发布** | 用此号的 Actions 额度出 win-x64/arm64 EXE 与 Release |

| 项 | 说明 |
|----|------|
| 当前 remote | `https://github.com/cknb6/BunnyCompanion.git`（小号仓，跑 Actions） |
| 大号权限 | 已加为仓库 **admin** 协作者（可 push / 改设置；CI 仍在 cknb6 额度下跑） |
| 贡献提交身份 | `user.name`：传康KK；`user.email`：**1837620622@qq.com**（绿点记到大号） |
| 推送方式 | 可用大号或小号凭据 push 到 `cknb6/BunnyCompanion`；**workflow 始终在 cknb6 下执行** |
| Actions | `.github/workflows/build-windows.yml`：`windows-latest`，矩阵 `win-x64` / `win-arm64`，自包含 publish + artifact/Release |
| 触发 | `push` 到 `main`/`master`，或 `workflow_dispatch` |
| 本机构建 | Windows：`一键构建Windows版.bat` 或 `.\Build-Windows.ps1 -Runtime win-x64` |
| macOS 交叉 | `dotnet build … -p:EnableWindowsTargeting=true`（仅编译，无 GUI） |

**没有用户明确命令不得擅自公开发布新仓。** 用户要求 push + Action 时：build 绿 → push 到 cknb6 仓 → 用小号额度出包。

## 目录要点

```
BunnyCompanion/
  BunnyCompanion/          # 主工程
    MainWindow.*           # 桌宠、点拖、气泡、全屏
    ChatWindow.*           # 微信风聊天、附件
    Engine/                # PetActionCatalog、MouseReactionCatalog
    Services/              # Agent / 记忆 / 工具箱 / 设置 / 卸载
    Models/PetSettings.cs
  .github/workflows/       # CI 打包
  tools/OfflineFallbackCheck/
  AGENTS.md                # 本文件
```

本地数据：`%LocalAppData%\BunnyCompanion\`
- `settings.json` — 设置与爱心
- `companion_memory.json` — 长期记忆（人物/偏好）
- 一键卸载会删整个配置目录

## Agent 工具链（必须正确）

```
用户消息
  → CompanionMemoryService.IngestUserUtterance（人物/偏好）
  → 系统提示 = AgentSystemPrompt + 记忆块 +（可选）定位/天气预取
  → 阶跃 step-3.7-flash + tools（主）
  → OpenRouter 免费模型 + tools
  → 阶跃纯文本
  → ChatReplyService 本地中文
```

| 工具 | 用途 |
|------|------|
| get_location | IP + 网卡定位 |
| get_weather | wttr.in；`FormatWeatherBroadcast` / `BuildWeatherAlerts` 含高温·降水警示 |
| list_dir / read_file / write_file / move_path / … | 本机文件 |
| run_command | PowerShell（有高危护栏） |
| get_clipboard / open_path / get_process_list | 其它本机能力 |

配置：`Services/AiConfig.cs`（密钥勿再扩散到新公开文案）。  
`app.manifest`：`highestAvailable`（有管理员则提升，便于 Agent 写更多路径）。

## 交互与记忆

- **点拖**：`WM_NCHITTEST` 像素命中 + 躯干软命中；拖拽捕获超时自愈；`MouseReactionCatalog` 3×3 分区 / 拖拽方向强度 / 滚轮。
- **记忆**：聊到人名写入 `People`；偏好/「记住…」写入 `Facts`；系统提示注入；人物/偏好/备忘气泡偶尔弹出（节流约 3.5 分钟）。
- **备忘录**：聊天「提醒我30分钟后…」「明天下午3点…」→ `Memos`；`memo_add/list/done` 工具；`ReminderTimer` 到期气泡。
- **星座**：`ZodiacService` + 工具 `zodiac_analyze`；生日/星座写入记忆画像。
- **今日卡**：`DailyCompanion.BuildDailyCard` / 工具 `daily_card`。
- **天气**：`get_weather` + `WeatherReport` 高温/降水/雷电等预警；上午可主动轻量天气气泡。
- **穿透**：`ClickThrough` 或托盘 Ctrl+Shift+P；开启时点不上是预期行为。

## 自检命令

```bash
# 离线词库
dotnet run --project tools/OfflineFallbackCheck/OfflineFallbackCheck.csproj -c Release

# 编译（macOS 加 EnableWindowsTargeting）
dotnet build BunnyCompanion.sln -c Release -p:EnableWindowsTargeting=true

# 记忆/天气等纯逻辑单测（tools/GoalVerify）
dotnet test tools/GoalVerify/GoalVerify.csproj -c Release
```

## 已知风险

- SmartScreen：无商业签名时「未知发布者」。
- 公网 IP 定位在公司出口可能偏到机房/总部城市。
- Actions 依赖 GitHub 账号 Actions 额度；billing 锁定会导致 `startup_failure`。
- 仓库若曾提交 API Key，轮换密钥并避免再进公开 README。
