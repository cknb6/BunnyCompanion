# 小申陪伴（BunnyCompanion）

面向 **Windows 10/11** 的桌面宠物：透明置顶、托盘控制、点击互动、粉嫩气泡，以及可聊天 / 看桌面的多模态 Agent。

- 语言 / 框架：**C# · WPF · .NET 8**
- 仓库：[`cknb6/BunnyCompanion`](https://github.com/cknb6/BunnyCompanion)（公开）
- 下载安装包：**[Releases](https://github.com/cknb6/BunnyCompanion/releases)**（Action 自动打包并发布）

---

## 下载与运行

1. 打开 [Releases](https://github.com/cknb6/BunnyCompanion/releases)
2. 下载最新版中的其一即可（内容相同）：
   - `BunnyCompanion-win-x64.exe`
   - `XiaoShenCompanion.exe`
3. 双击运行

**自包含单文件**：体积大约 **100MB 级**，已内置 .NET 桌面运行时，**对方无需再安装 .NET**。

若 Windows SmartScreen 提示未知发布者：点「更多信息」→「仍要运行」（个人项目未购买代码签名证书时的常见提示）。

也可参考仓库内 `使用说明.txt`。

---

## 功能一览

| 类别 | 内容 |
|------|------|
| 桌宠壳 | 透明无边框、始终置顶、系统托盘、启动入场动画 |
| 素材 | 48 个透明动作（待机 / 走 / 跳 / 摸头 / 比心 / 睡 / 读 / 喝水 / 生日等） |
| 点击 | 头 / 身 / 脚分区反馈；双击比心；中键打开聊天；拖拽移动；右键菜单 |
| 命中 | 按精灵透明度做像素级命中，空白区域不挡桌面点击 |
| 快捷键 | `Ctrl+Shift+S` 显隐 · `C` 聊天 · `P` 穿透 · `,` 设置 · `H` 帮助 |
| 行为 | 自动散步与动作互斥、多显示器 DPI、全屏可自动隐藏 |
| 气泡 | 渐变描边阴影 + 淡入上浮 |
| 陪伴 | 喝水 / 休息提醒、25 分钟专注、生日与纪念日、安静时段、开机启动 |
| 数据 | 爱心值、互动次数、位置与设置保存在本机；单实例 |

### Agent 对话链路

```text
阶跃 step-3.7-flash（主：聊天 + 看桌面）
    → OpenRouter 免费模型（在线兜底）
    → 本地中文关键词（断网 / 全挂时）
```

- 看桌面为**用户主动触发**（聊天窗「看桌面」或相关意图），非后台连环截屏
- 回复强制**简体中文**（系统提示约束）

---

## 快捷键

| 组合 | 作用 |
|------|------|
| `Ctrl+Shift+S` | 显示 / 隐藏桌宠 |
| `Ctrl+Shift+C` | 打开聊天 |
| `Ctrl+Shift+P` | 切换鼠标穿透 |
| `Ctrl+Shift+,` | 个性化设置 |
| `Ctrl+Shift+H` | 快捷键说明 |

与托盘菜单语义一致。

---

## 目录结构

```text
BunnyCompanion/
├─ BunnyCompanion.sln
├─ BunnyCompanion/                 # WPF 主项目
│  ├─ Assets/Sprites/              # 48 个透明精灵
│  ├─ Engine/                      # 动作目录
│  ├─ Models/                      # 本地设置
│  └─ Services/                    # Agent / 屏幕 / 启动项 / 热键等
├─ Artwork/                        # 角色母版与动作表（设计用）
├─ tools/                          # 校验与素材工具
├─ .github/workflows/              # Windows 打包 + 自动 Release
├─ Build-Windows.ps1
├─ 一键构建Windows版.bat
└─ 使用说明.txt
```

参考照片只用于角色设计，不打进 EXE。`Artwork` 中为衍生 Q 版素材。

---

## 本地构建（可选）

环境任选其一：

- Visual Studio 2022（「.NET 桌面开发」工作负载），或  
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bat
一键构建Windows版.bat
```

或：

```powershell
.\Build-Windows.ps1 -Runtime win-x64 -Configuration Release
```

成功后产物：

```text
可直接发送/小申陪伴.exe
可直接发送/使用说明.txt
可直接发送/版本校验.txt
```

默认 **`SelfContained=true` 单文件**：约 100MB 级，接收者免装 .NET。

---

## CI / Release

- 工作流：`.github/workflows/build-windows.yml`
- 触发：`push` 到 `main` 或手动 `workflow_dispatch`
- 步骤：校验 → `Build-Windows.ps1` 自包含发布 → 上传 Artifact → **自动创建 GitHub Release**（tag 形如 `v1.1.<run_number>`）

查看运行记录：[Actions](https://github.com/cknb6/BunnyCompanion/actions)  
下载成品：[Releases](https://github.com/cknb6/BunnyCompanion/releases)

---

## 本地数据

| 项目 | 位置 |
|------|------|
| 设置 / 爱心值 / 位置 | `%LocalAppData%\BunnyCompanion\settings.json` |
| 崩溃日志 | `%LocalAppData%\BunnyCompanion\Logs\` |
| 开机启动 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（仅当前用户） |

不写系统目录、不要求管理员权限。

---

## 增加动作

1. 将透明 PNG 放入 `BunnyCompanion/Assets/Sprites`
2. 在 `Engine/PetActionCatalog.cs` 注册帧与时长
3. 在托盘菜单或随机行为中增加入口
4. 重新构建 / 等 Action 出包

推荐 **384×384 RGBA PNG**，脚底基线对齐。

---

## 说明

- 体积偏大是因为 **自包含** 打包把 .NET / WPF 运行时打进 EXE，以便双击即用。
- 若改为框架依赖可把 EXE 压到约 10MB 级，但对方必须另装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。
- 密钥写在源码中时请注意公开仓库可见；不需要的 key 请自行轮换。

---

## License

私人 / 礼物用途以仓库说明为准；转载与商用请先取得授权。
