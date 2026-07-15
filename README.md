# 小申陪伴（BunnyCompanion）

面向 Windows 10/11 的私人定制桌面宠物（对标 Shimeji / VPet 等经典桌宠的托盘、穿透、拖拽、气泡与快捷操作范式）。  
C# / WPF / .NET 8。默认发布为**框架依赖单文件**（约 10MB 级，功能不变）；目标机需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

## 已实现功能

- 透明无边框、始终置顶、系统托盘、启动入场动画
- 48 个透明动作素材；像素级命中，空白区域不挡点击
- 单击分区（头/身/脚）、双击比心、中键聊天、拖拽、右键菜单
- 全局快捷键：Ctrl+Shift+S/C/P/,/H（显隐/聊天/穿透/设置/帮助）
- 自动行为状态机、散步与动作互斥、多显示器 DPI 适配
- 粉嫩对话气泡（渐变+阴影+入场动画）
- 多模态 Agent：step-3.7-flash → OpenRouter 免费模型 → **本地中文兜底**
- 主动「看桌面」截图理解（用户触发，非后台连环截屏）
- 喝水/休息提醒、25 分钟专注、生日与纪念日、安静时段
- 角色缩放、鼠标穿透、全屏自动隐藏、开机启动
- 本地保存爱心值、互动次数、位置与设置；单实例与崩溃日志

## 目录结构

```text
BunnyCompanion/
├─ BunnyCompanion.sln                 Visual Studio 解决方案
├─ BunnyCompanion/                    WPF 主项目
│  ├─ Assets/Sprites/                 48 个透明精灵
│  ├─ Engine/                         动作状态定义
│  ├─ Models/                         本地设置模型
│  └─ Services/                       启动项、屏幕与设置服务
├─ Artwork/                           角色母版、透明动作表与素材清单
├─ tools/                             素材切分与工程校验工具
├─ Build-Windows.ps1                  正式发布脚本
├─ 一键构建Windows版.bat              双击构建入口
└─ 使用说明.txt                       可直接发给使用者的说明
```

参考照片只用于角色设计，不会复制进程序或最终 EXE。`Artwork` 中保留的是衍生的 Q 版角色素材，方便以后增加动作。

## 在 Windows 上构建

准备下列任意一种环境：

- Visual Studio 2022，并安装“.NET 桌面开发”工作负载；或
- .NET 8 SDK。

双击 `一键构建Windows版.bat`。构建成功后，最终文件位于：

```text
可直接发送/小申陪伴.exe
```

也可以在 PowerShell 中运行：

```powershell
.\Build-Windows.ps1 -Runtime win-x64 -Configuration Release
```

构建脚本执行 `dotnet publish`：**框架依赖 + 单文件 + 压缩**，体积约 10MB 级；WPF 不裁剪以保证功能稳定。生成 SHA-256 校验值。

接收者需安装一次 .NET 8 Desktop Runtime（微软官方）。若改回自包含（对方免装运行库），体积会回到约 100MB 级。

## 本地数据

运行时配置保存在：

```text
%LocalAppData%\BunnyCompanion\settings.json
```

开机启动只使用当前用户的注册表位置：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

程序不请求管理员权限，不写入系统目录，也不包含网络请求代码。

## 后续增加动作

1. 将新的透明 PNG 放入 `BunnyCompanion/Assets/Sprites`。
2. 在 `Engine/PetActionCatalog.cs` 注册动作帧和时长。
3. 在托盘菜单或随机行为列表中添加触发入口。
4. 重新运行一键构建脚本。

动作图片推荐使用 384 × 384 RGBA PNG，并保持角色脚底基线一致。
