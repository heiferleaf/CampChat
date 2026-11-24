# 贡献指南

欢迎通过 GitHub Issues 提交可复现的问题，也欢迎提交小范围的改进。游戏窗口相关问题请附 Windows 版本、显示模式和发生问题的操作顺序。聊天连接问题请注明是否使用局域网或虚拟局域网。请用示例数据替换真实房间码。

## 本地检查

需要 Windows 和 .NET 10 SDK。发布前运行网络检查和桌面交互检查。

```powershell
dotnet build .\CampChat\CampChat.csproj -c Release
dotnet run --project .\CampChat.Check\CampChat.Check.csproj -c Release
dotnet run --project .\CampChat.Overlay.Check\CampChat.Overlay.Check.csproj -c Release
dotnet build .\CampChat.Preview\CampChat.Preview.csproj -c Release
dotnet run --project .\CampChat.Preview.Check\CampChat.Preview.Check.csproj -c Release
```

桌面交互检查需要可用的 Windows 桌面会话。`CampChat.Preview` 可独立运行，用来模拟消息、打字和字幕布局。更改外观时请注明已检查的窗口尺寸、键盘操作和动效关闭状态。

## 发布版本

将应用版本和变更记录更新到同一版本号后，运行本地检查。用 `dotnet publish` 生成轻量 Windows x64 包，附上使用说明和许可证。发布对应版本标签与 ZIP，发布说明列出改动、运行时要求和验证范围。
