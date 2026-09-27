# Windows 发布构建

前提：.NET 8 SDK、Node.js 22+（推荐随交付使用的 24）、pnpm 11，以及可选的 Inno Setup 6.7+ 编译器。

```powershell
.\Packaging\Build.ps1 -InnoCompiler 'C:\Tools\InnoSetup\ISCC.exe' -WebViewBootstrapper 'C:\Tools\MicrosoftEdgeWebview2Setup.exe'
```

安装器默认安装到当前用户的 `%LOCALAPPDATA%\Programs\IslandUI`，创建桌面/开始菜单快捷方式并启动。卸载通过 Windows 应用列表完成；聊天数据和 Bot 配置放在独立用户目录中，不随卸载删除。

WebView2 引导程序必须从微软官方下载且签名验证通过；缺少该组件时首次安装需要网络。程序本身自带 .NET 和 Node，不修改系统 PATH。

用于隔离部署测试的参数：`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PORTABLE=1 /DIR="测试目录"`。便携测试不创建快捷方式、卸载注册项或自动启动，也不安装系统组件。

源码中保留可选的 Docker 计算/浏览器环境，不自动下载 Docker 或建立外部账号连接。代码执行等相关功能仍依赖用户配置的原有环境。

官方说明：

- [WebView2 分发与安装](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution)
- [Inno Setup 下载与许可](https://jrsoftware.org/isdl.php)

该安装器尚未使用发布者代码签名证书签名；SHA-256 文件用于校验传输完整性，不等同于数字签名。商业发布前还应检查依赖许可和签名要求。
