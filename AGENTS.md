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
    MainWindow.*           # 桌宠、点拖、气泡、全屏、系统触发器定时器
    ChatWindow.*           # 微信风聊天、附件、语音输入/TTS
    Engine/                # PetActionCatalog、MouseReactionCatalog
    Services/              # Agent / 记忆 / 工具箱 / 设置 / 卸载 / 监控 / 浏览器 / 技能 / 语音
      AiAgentService.cs    # 四级降级 Agent 主链路
      AiConfig.cs          # 接口与密钥（internal，勿扩散）
      WindowsAgentToolkit.cs  # 30+ 本机工具定义与执行
      CompanionMemoryService.cs / LocalAgentMdStore.cs  # 双层记忆
      SystemMonitorService.cs / SystemTriggerConfig.cs  # 系统监控触发器
      BrowserService.cs    # 网页抓取与浏览器
      SkillPluginService.cs  # Markdown 技能插件
      VoiceService.cs      # TTS（阶跃在线+SAPI离线）+ ASR（SAPI）
    Models/PetSettings.cs  # 含 SystemTriggers/TtsEnabled/VoiceInputEnabled
  Artwork/                 # 角色素材表 + Screenshots/（演示截图）
  .github/workflows/       # CI 打包（build-windows.yml）
  tools/GoalVerify/        # 纯逻辑自检（Exe）
  tools/OfflineFallbackCheck/  # 离线词库自检（Exe）
  tools/validate_project.py    # 结构校验（XML/精灵/动作引用）
  AGENTS.md                # 本文件
```

本地数据：`%LocalAppData%\BunnyCompanion\`
- `settings.json` — 设置、爱心、互动次数、窗口位置、SystemTriggers/TtsEnabled/VoiceInputEnabled
- `companion_memory.json` — 结构化长期记忆（人物/偏好/备忘/星座）
- **`agent.md`** — 对话**自动摘要压缩**的 Markdown 长期记忆（滚动摘要 + 近期压缩 + 用户手写备注）；每轮聊天后更新，超长自动折叠
- `skills/` — Markdown 技能插件目录（首次运行自动生成示例技能）
- `Logs/crash.log` — 崩溃日志
- 一键卸载会删整个配置目录
- 托盘：「打开长期记忆 agent.md」「打开本地配置目录」

## Agent 工具链（必须正确）

```
用户消息
  → CompanionMemoryService.IngestUserUtterance（人物/偏好/备忘/星座）
  → 系统提示 = AgentSystemPrompt + 结构化记忆块 + agent.md 摘要块 +（可选）定位/天气/星座预取
  → 阶跃 step-3.7-flash + tools（主，工具循环 MaxToolRounds=8）
  → OpenRouter 免费模型 + tools（文本/视觉两组，按序尝试）
  → 阶跃纯文本（无 tools）
  → ChatReplyService 本地中文（断网最终兜底）
  → 每轮结束 AppendTurnDigest 写 agent.md + SyncAgentMdFromMemory
```

| 工具 | 用途 |
|------|------|
| get_location | IP + 网卡定位 |
| get_weather | wttr.in；`FormatWeatherBroadcast` / `BuildWeatherAlerts` 含高温·降水警示 |
| list_dir / read_file / write_file / move_path / … | 本机文件 |
| run_command | PowerShell（有高危护栏） |
| get_clipboard / open_path / get_process_list | 其它本机能力 |
| get_system_monitor | CPU/内存/电池/闲置快照（`SystemMonitorService`） |
| fetch_url / web_search / read_browser_tab / open_url | 网页抓取与浏览器（`BrowserService`） |
| skill_list / skill_get / skill_run | 技能插件（`SkillPluginService`，Markdown 技能目录） |

配置：`Services/AiConfig.cs`（密钥勿再扩散到新公开文案）。  
`app.manifest`：`highestAvailable`（有管理员则提升，便于 Agent 写更多路径）。

### 界面不暴露 API 规则（重要）
- 用户可见文本（状态栏、气泡、关于框、托盘菜单）**不得出现** step/阶跃/OpenRouter/模型名/API key 等接口细节。
- `ChatWindow.ShortProvider` 把内部 provider 统一转成「在线」「本地陪伴」。
- `AiAgentService` 的 progress 文案用「小申 Agent 思考中…」「正在换条线路想想…」，不报模型名。
- 模型名/key 只存在于 `AiConfig.cs`（internal）与后台调用，不进 UI。

## 交互与记忆

- **点拖**：`WM_NCHITTEST` 像素命中 + 躯干软命中；拖拽捕获超时自愈；`MouseReactionCatalog` 3×3 分区 / 拖拽方向强度 / 滚轮。
- **记忆**：聊到人名写入 `People`；偏好/「记住…」写入 `Facts`；系统提示注入；人物/偏好/备忘气泡偶尔弹出（节流约 3.5 分钟）。
- **备忘录**：聊天「提醒我30分钟后…」「明天下午3点…」→ `Memos`；`memo_add/list/done` 工具；`ReminderTimer` 到期气泡。
- **星座**：`ZodiacService` + 工具 `zodiac_analyze`；生日/星座写入记忆画像。
- **今日卡**：`DailyCompanion.BuildDailyCard` / 工具 `daily_card`。
- **天气**：`get_weather` + `WeatherReport` 高温/降水/雷电等预警；上午可主动轻量天气气泡。
- **穿透**：`ClickThrough` 或托盘 Ctrl+Shift+P；开启时点不上是预期行为。

## 新增四大功能（v1.2）

- **系统监控触发器**（`SystemMonitorService` + `SystemTriggerConfig`）：CPU/内存过高、低电量、久坐离开 → 桌宠自动提醒+动作。`MainWindow.SystemTriggerTimer` 2 分钟采样，节流冷却避免刷屏，安静时段不打断。配置在 `PetSettings.SystemTriggers`。
- **浏览器控制+网页总结**（`BrowserService`）：`fetch_url` 抓网页正文（去标签）、`web_search` 打开搜索、`read_browser_tab` 读前台浏览器标签、`open_url` 打开网址。纯 HttpClient，无 NuGet。
- **技能插件系统**（`SkillPluginService`）：`%LocalAppData%\BunnyCompanion\skills\` 下 Markdown 技能（frontmatter: name/description/triggers/command + 正文指令）。`skill_list/get/run` 工具。内置清理临时文件、今日待办、打开常用示例。`CompanionRuntime.Skills` 单例。
- **语音输入+TTS**（`VoiceService`）：TTS 优先阶跃在线（`step-tts-mini`，复用 `StepApiKey`，真人级）→ SAPI 离线兜底；ASR 用 SAPI（PowerShell 调 `System.Speech.Recognition`）。`PetSettings.TtsEnabled`/`VoiceInputEnabled` 开关。界面只显示「语音输入/朗读」。

## 体验与可靠性升级（v1.3）

- Agent 支持多张上传图片与桌面截图同时进入视觉请求；`UsedDesktopImage` 只表示桌面截图确实成功加入请求。
- 聊天生成期间发送按钮切换为“停止”，也可按 Esc 取消；取消后回滚未回答的历史消息。
- 语音、CPU/内存/电量/久离返回阈值、冷却时间均有设置入口；久离提醒在用户返回后显示。
- 聊天与设置窗口按当前显示器工作区缩放，快捷键冲突可在帮助中看到，跨显示器全屏不再误隐藏桌宠。
- PowerShell 工具统一使用 `-EncodedCommand`，外部命令支持真实超时与取消。
- CI 在发布前执行三套自检，并分别验证 win-x64/win-arm64 校验文件与最终 EXE 的 SHA256。

## 版本号（单一来源）

- 唯一来源：`BunnyCompanion.csproj` 的 `<Version>`。
- `AppCredits.VersionLabel` 运行时读程序集版本，不再硬编码。
- CI Release tag = `v<Version>.<run_number>`，从 csproj 提取，不写死。
- `Build-Windows.ps1` 校验文件头也从 csproj 读版本。

## 自检命令

```bash
# 离线词库
 dotnet run --project tools/OfflineFallbackCheck/OfflineFallbackCheck.csproj -c Release

# 编译（macOS 加 EnableWindowsTargeting）
dotnet build BunnyCompanion/BunnyCompanion.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true --self-contained false

# 记忆/天气/鼠标/agent.md 等纯逻辑自检（tools/GoalVerify，是 Exe 不是测试框架）
dotnet run --project tools/GoalVerify/GoalVerify.csproj -c Release

# 结构校验（XML/XAML 事件/48 精灵/动作引用/可移植性）
python3 tools/validate_project.py
```

### 测试项目源码链接（新增源文件需同步加入 csproj）
- `tools/GoalVerify/GoalVerify.csproj` 链接：CompanionMemoryService / CompanionRuntime / **SkillPluginService** / LocalAgentMdStore / WeatherReport / ZodiacService / DailyCompanion / MouseReactionCatalog / PetSettings / **SystemTriggerConfig**。
- `tools/OfflineFallbackCheck/OfflineFallbackCheck.csproj` 链接：PetSettings / **SystemTriggerConfig** / ChatReplyService。
- 凡 `PetSettings` 或 `CompanionRuntime` 引用的新类型（如 `SystemTriggerConfig`、`SkillPluginService`），必须把其定义文件加入测试项目，否则测试编译失败。**不要链接含 `System.Windows.Forms`/WPF 的文件**（如 `SystemMonitorService`/`VoiceService`），测试项目是 net9.0 非 Windows。

## 已知风险

- SmartScreen：无商业签名时「未知发布者」。
- 公网 IP 定位在公司出口可能偏到机房/总部城市。
- Actions 依赖 GitHub 账号 Actions 额度；billing 锁定会导致 `startup_failure`。
- 仓库若曾提交 API Key，轮换密钥并避免再进公开 README。
- 阶跃在线 TTS/ASR 需联网且 key 有效；断网或 key 失效时 TTS 回退 SAPI 系统语音，ASR 仅 SAPI 可用。
- `read_browser_tab` 当前仅返回前台浏览器进程名+标题，地址栏 URL 受 UI Automation 限制未实现，引导用户用 `fetch_url`。
