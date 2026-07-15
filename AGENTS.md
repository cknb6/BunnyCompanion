# 小申陪伴（BunnyCompanion）— 项目交接

面向 **Windows 10/11** 的 WPF 桌面宠物 + 本机 Agent。macOS 上只改代码/交叉编译，**不能**在本机跑 WPF EXE。

## 产品

| 项 | 值 |
|----|-----|
| 产品名 | 小申陪伴 |
| 工程名 | BunnyCompanion |
| 技术 | C# · WPF · .NET 8 · 自包含单文件 EXE |
| 默认桌宠名 | 小申 |
| 默认用户称呼 | **宝宝**（历史「宝贝」在 Normalize 时会迁到「宝宝」） |

## 作者与联系（勿混用邮箱）

| 场景 | 内容 |
|------|------|
| GitHub / PR / push 提交 | `user.name`：传康Kk 或 `传康KK（万能程序员）`；`user.email`：**1837620622@qq.com**（账号 **1837620622**） |
| 文档/交付/用户向署名 | 微信：1837620622（传康Kk）· 邮箱：**2040168455@qq.com** · 咸鱼/B站：万能程序员 |
| 禁止 | 把 `2040168455@qq.com` 写进 GitHub commit author；论文署名不要改成 GitHub 邮箱 |

## 仓库与发布

| 项 | 说明 |
|----|------|
| 当前 remote（构建/Actions） | `https://github.com/cknb6/BunnyCompanion.git`（公开仓曾用于 Actions 出包） |
| 贡献身份 | push/commit 用 **1837620622** 的已验证邮箱，保证绿点记到本人 |
| Actions | `.github/workflows/build-windows.yml`：`windows-latest`，矩阵 `win-x64` / `win-arm64`，`Build-Windows.ps1` 自包含 publish，上传 artifact 并可由 release job 汇总 |
| 触发 | `push` 到 `main`/`master`，或 `workflow_dispatch` |
| 本机构建 | Windows：`一键构建Windows版.bat` 或 `.\Build-Windows.ps1 -Runtime win-x64` |
| macOS 交叉 | `dotnet build BunnyCompanion.sln -c Release -p:EnableWindowsTargeting=true`（仅编译验证，不出可跑 GUI） |

**没有用户明确命令不得擅自公开发布新仓。** 用户已要求 push + Action 时：提交前 `dotnet build` 绿，再 push，并记录 workflow run。

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
- **记忆**：聊到人名写入 `People`；偏好/「记住…」写入 `Facts`；系统提示注入；`TryPickPersonBubble` 约 28% 概率 + 主窗 4 分钟节流，气泡偶尔提人。
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
