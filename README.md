# 小申陪伴（BunnyCompanion）

> **本软件由传康KK开发**（传康Kk / 万能程序员）  
> 微信：1837620622 · 邮箱：2040168455@qq.com · 咸鱼/B站：万能程序员

面向 **Windows 10/11** 的桌面宠物：透明置顶、托盘、点击互动、粉嫩气泡，以及可聊天 / 看桌面的多模态 Agent。

| | |
|--|--|
| 开发者 | **传康KK** |
| GitHub 大号 | [1837620622](https://github.com/1837620622)（身份 / 协作；Actions 额度不足时不用于打包） |
| GitHub 小号 | [cknb6](https://github.com/cknb6)（**仓库托管 + Actions 打包发布**） |
| 技术 | C# · WPF · .NET 8 |
| 仓库 | [cknb6/BunnyCompanion](https://github.com/cknb6/BunnyCompanion) |
| 下载 | **[Releases](https://github.com/cknb6/BunnyCompanion/releases)**（由小号 Action 自动打包） |

---

## 下载哪个文件？

Release 里按 **CPU 架构** 提供 **两个** 自包含 EXE（不是同一个文件复制很多遍）：

| 下载这个 | 适合谁 |
|----------|--------|
| **BunnyCompanion-win-x64.exe** | **绝大多数电脑**（Intel / AMD）。Windows on ARM 多数也能靠兼容层运行 x64 版 |
| **BunnyCompanion-win-arm64.exe** | **原生 ARM64** 的 Windows 设备（部分 Surface / 骁龙本），要更好性能时选它 |

另外可能带有中文名 `小申陪伴-x64.exe` / `小申陪伴-arm64.exe`，与对应英文名是**同一架构各一份**。

- **一般用户：只下 x64 即可。**  
- **不要**以为要下齐所有 exe；以前 Release 里多个英文名曾是**同一 x64 文件的重复拷贝**，现已改成「一架构一文件」。

### 运行

- **自包含**，体积大约 **70～100MB / 每个架构**  
- **无需安装 .NET**，双击运行  
- SmartScreen：更多信息 → 仍要运行  

说明文件：`使用说明.txt` / Release 中的 `README-zh.txt`。

---

## 为什么不能「一个 EXE 通吃 x86 + ARM」？

| 说法 | 实际情况 |
|------|----------|
| 自包含 .NET / WPF | 必须按 **RID** 分别发布：`win-x64`、`win-arm64`（以及少见的 `win-x86`） |
| 一个「通用原生」EXE | **做不到**（Windows 不是 macOS 那种 fat binary 生态） |
| 多数安卓/商店「通用」 | 往往是安装包内含多架构，安装时再选，不是单个自包含 WPF EXE |

因此本仓库采用：**同一 Release 里放 x64 + arm64 两个包**，下载时选一个。

---

## 功能概要

- 透明置顶、托盘、启动动画、48 个透明动作  
- 头/身/脚点击分区、双击比心、中键聊天、拖拽、右键菜单  
- 像素级透明命中（空白不挡桌面）  
- 快捷键：`Ctrl+Shift` + `S` 显隐 / `C` 聊天 / `P` 穿透 / `,` 设置 / `H` 帮助  
- 自动行为、多显示器 DPI、全屏可隐藏、喝水/休息提醒、专注 25 分钟、纪念日  
- 本地保存设置与爱心值；单实例  
- **一键卸载**：右键菜单清除启动项、本地数据，并尝试删除 EXE  


### Agent

```text
阶跃 step-3.7-flash（主：聊天 + 看桌面）
  → OpenRouter 免费模型（在线兜底）
  → 本地中文关键词（断网兜底）
```

看桌面需用户主动触发，不会后台连环截屏。

---

## 本地构建

需要 .NET 8 SDK 或 VS2022「.NET 桌面开发」。

```bat
一键构建Windows版.bat
```

或指定架构：

```powershell
.\Build-Windows.ps1 -Runtime win-x64
.\Build-Windows.ps1 -Runtime win-arm64
```

产物目录：`可直接发送\`。

---

## CI

- 工作流：`.github/workflows/build-windows.yml`  
- 矩阵构建：`win-x64` + `win-arm64`  
- 成功后自动 **GitHub Release**（tag `v1.1.<run_number>`）  

[Actions](https://github.com/cknb6/BunnyCompanion/actions) · [Releases](https://github.com/cknb6/BunnyCompanion/releases)

---

## 本地数据

- 设置：`%LocalAppData%\BunnyCompanion\settings.json`  
- 日志：`%LocalAppData%\BunnyCompanion\Logs\`  
- 开机启动：当前用户 `HKCU\...\Run`  

---

## 开发者

**本软件由传康KK开发。**

- 微信：1837620622（传康Kk）
- 邮箱：2040168455@qq.com
- 咸鱼 / B站：万能程序员
- GitHub **大号**：[1837620622](https://github.com/1837620622)（开发者身份；commit 用 `1837620622@qq.com`）
- GitHub **小号**：[cknb6](https://github.com/cknb6)（因大号 Actions 额度不足，**用小号仓库跑 CI / 打 EXE / 发 Release**）

## License

以仓库说明为准；转载 / 商用请先联系传康KK授权。
