# 安全设计与审计清单

本清单针对构建后的主程序 `SafeCodexQuotaWidget.exe` 在运行期间。`Build.ps1`、图标/预览/动画测试工具和 `OpenCodexWithQuota.exe` 属于单独的构建或启动辅助程序，不包含在下面的运行时禁止项内。

## 允许的能力

1. 在两个受限目录内查找 `codex.exe`。
2. 离线验证 `codex.exe` 的 Authenticode 签名和 OpenAI 发布者名称。
3. 直接启动 `codex.exe app-server --listen stdio://`，不经过 shell。
4. 通过子进程标准输入/输出请求 `account/rateLimits/read`。
5. 在内存中显示返回的百分比、重置时间和计划类型。

## 明确禁止的能力

- 任意网络请求、监听端口或 WebView。
- 读取 Codex 认证文件、环境变量中的 Token 或浏览器凭据。
- 启动任意路径、未签名或非 OpenAI 签名的程序。
- 写文件、写日志、修改注册表、开机自启、计划任务、服务或自动更新。
- 加载第三方 DLL、NuGet、npm 包或远程代码。

## 仍需知道的边界

- Windows 信任链和 OpenAI 代码签名是识别 `codex.exe` 的信任根。
- 本程序不保证 Codex 服务未来永远保持相同的 `app-server` 协议；协议改变时只会显示读取失败。
- 本地编译的悬浮窗未做商业代码签名，因此不能依靠 SmartScreen 发布者信息判断它。
