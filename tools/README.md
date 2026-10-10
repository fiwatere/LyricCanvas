# 开发工具

在项目根目录使用 .NET 8 SDK。

- `verify-all.ps1`：构建解决方案并运行离线回归检查。
- `publish-portable.ps1`：生成 Windows x64 自包含目录、ZIP 和 SHA-256 文件。

```powershell
dotnet build LyricCanvas.sln
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-all.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish-portable.ps1
```
