# 离线授权与开发者签发

## 规则

JP-Vision 采用 ECDSA P-256 / SHA-256 数字签名许可，signature 为 64 字节 IEEE P1363。客户程序仅内置验签公钥，只有持有匹配签发私钥的开发者能创建有效许可证。

授权绑定电脑、永久有效，不联网、不登录、不设到期日期。同机允许多个工位、会话、WPF 进程和 Windows 用户，不以进程数限制使用。Studio 必须获得 studio 功能，原生运行必须获得 runtime 功能；直接调用 C ABI 同样检查。

机器身份优先为标准化 SMBIOS UUID 的 SHA-256，UUID 不可用时选择主板制造商与序列号 profile；常见空值、全零、全 F 和占位字符串拒绝。请求只携带 profile、算法版本与摘要，不包含原始硬件编号。验证按照签发时的 profile 比较本机对应来源，不会因之后出现优先级更高的来源改变授权身份。

硬件身份不可用时返回 MachineIdUnavailable，不自动绑定用户名、MAC、随机 GUID 或客户自己提交的机器码。运行库没有可传入假机器身份的生产接口。

## 客户流程

1. 客户启动 Studio 激活页，或使用 WPF LicensePanel 导出 `.vreq` 请求。
2. 客户将请求给你，你在开发电脑签发 `.vlic`。
3. 客户从激活页导入许可证。原生代码先验签和确认本机，再持久化；重启后仍有效。

开发者命令，在项目目录执行：

```powershell
dotnet .\tools\LicenseIssuer\bin\Release\net10.0-windows\JPVision.LicenseIssuer.dll issue `
  --request C:\CustomerRequests\customer-A.vreq `
  --key "$env:USERPROFILE\.jp-vision\issuer\owner.private" `
  --out C:\CustomerLicenses\customer-A.vlic --customer "客户A"
```

只把签发的许可证交给对应客户。LicenseIssuer、owner.private 和备份密码均留在开发者电脑，不进入客户端目录、NuGet、邮件模板或聊天。客户机器请求不是执行权证明，最终执行权来自签名及本机硬件匹配。

`inspect --license <file>` 只显示字段，并不确认签名或机器授权；真正的校验应使用 Studio / SDK / Runtime。

## 私钥、公钥与备份

本次 owner.private 在项目外，内容经 Windows DPAPI CurrentUser 加密，并设置仅当前用户访问的文件 ACL。公钥在 `config/trusted-key.json` 与 `trusted_key.h`，公钥不是秘密；Runtime 编译时固定该信任根。

不能仅将 DPAPI 私钥文件复制到另一 Windows 账户或重装后的系统并期望解密。需先执行加密备份，口令在本地终端隐式输入，至少 12 字符：

```powershell
dotnet .\tools\LicenseIssuer\bin\Release\net10.0-windows\JPVision.LicenseIssuer.dll backup `
  --key "$env:USERPROFILE\.jp-vision\issuer\owner.private" --out E:\SecureBackup\owner-encrypted.pem
```

同时保留公开的 `trusted-key.json`。在目标开发账户恢复时，指定不存在的私钥路径，输入备份口令；工具验证恢复密钥的公钥与原信任根相同，再重新 DPAPI 加密：

```powershell
dotnet .\tools\LicenseIssuer\bin\Release\net10.0-windows\JPVision.LicenseIssuer.dll restore `
  --backup E:\SecureBackup\owner-encrypted.pem --public .\config\trusted-key.json `
  --key "$env:USERPROFILE\.jp-vision\issuer\owner.private"
```

恢复不覆盖已有私钥。不应重新 keygen 并替换已发布的信任根：旧客户许可证会与新公钥不匹配。首版只支持一套签发公钥，未实现多根轮换。

## 许可存储、更新和换机

默认是 `%ProgramData%\JP-Vision\license.vlic`。新建目录会保护写入权限，普通机器用户可读取。根据客户的 IT 策略，由有权写入该目录的账户执行激活；写入失败会报告 StorageAccessDenied，不误报成功。SDK 的 licenseStore 可指定其他受控目录，仍需完整签名和机器校验。

更新应用复制新的程序目录即可，发布包不携带许可证，因此不会替换 ProgramData 中已有授权。同机项目目录或 WPF 可执行文件目录迁移不改变硬件绑定。重装 Windows 后硬件来源保持一致时许可通常仍可验证，但应由客户重新导入备份许可证；真实重装场景本次未实测。

更换主板、机器或虚拟机身份后，导出新请求并正常签发。纯离线永久授权无法即时远程撤销旧电脑的许可证，也无法可靠识别完整克隆的虚拟机；此版本没有做这种保证。

错误导入不覆盖有效旧许可。导入入口要求许可包含本版本运行必需的 runtime 功能；不含该功能的签名文件在写入前拒绝，保留原有授权。超长、损坏、重复字段、签名篡改、异机、错误产品、未知 keyId 或版本被拒绝。若本机验证公钥不同，应核对客户安装包版本和信任根，不能用关闭验签解决。

## 开发和测试

`activate-dev.ps1` 正常签发本机开发许可证，只有加 `-Import` 才导入机器默认目录。`test.ps1` 在独立测试目录使用真实签名的负例检验错误机器、产品、keyId、版本与功能。负例不包含私钥，也不提供执行绕过。

本次已有跨语言 .NET 签发 → C++ CNG 验签证据与异机摘要模拟拒绝证据；不是在第二台真实机器或另一个 Windows 用户完成的实测。备份和恢复命令已实现，互动输入口令的完整往返需要你在本地按自己的备份策略操作。
