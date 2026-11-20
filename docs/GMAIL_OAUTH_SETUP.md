# Gmail OAuth 2.0 永久长期授权配置指南

LumeDesk 的收件箱小部件支持两种模式：
1. **Google OAuth 2.0（推荐）**：无需开启两步验证或生成应用专用密码，浏览器点一次「允许」即可拿到只读（`gmail.readonly`）长期授权，凭据由 Windows DPAPI 本机加密保存。
2. **标准 IMAP SSL**：适用于 QQ 邮箱、163 邮箱，或已开启 Gmail 应用专用密码的用户。

下文介绍如何花 3 分钟在自己的 Google Cloud 账号下配置一个 **永久有效（不 7 天过期）** 的桌面端 OAuth 客户端。

---

## 第一步：创建项目并启用 Gmail API

1. 打开 [Google Cloud Console](https://console.cloud.google.com/)，在顶部选择或新建一个项目（例如 `LumeDesk`）。
2. 左侧菜单进入 **「API 和服务」→「库」**，搜索 **`Gmail API`** 并点击 **「启用」**。

## 第二步：配置品牌信息与受众（防止 7 天过期）

1. 左侧菜单进入 **「Google Auth Platform（或 OAuth 权限请求页面）」→「品牌」**：
   - 应用名称：填 `LumeDesk`（任意名称）
   - 用户支持电子邮件：选你自己的 Gmail
   - **应用首页 / 隐私权政策链接**（发布正式版必填）：可以填你的个人主页或 GitHub 仓库链接，例如 `https://github.com/<你的用户名>/<仓库名>`
   - **已获授权的网域**：点击「添加网域」，填入对应域名（例如 `github.com`）
   - 开发者联系信息：填你自己的 Gmail，点 **「保存」**。
2. 左侧菜单进入 **「受众群体」**：
   - 用户类型选择 **「外部」**。
   - **关键步骤**：点击 **「发布应用」** 将状态从「测试」切换为 **「正式版（In production）」**。
     - ⚠️ 若停留在「测试」模式，Google 签发的 Refresh Token 会在 **7 天后强制过期**。
     - ✅ 切换到「正式版」后，自用无需提交 Google 审核，签发的 Refresh Token **永久有效**（仅在首次授权时浏览器会出现一次「Google 尚未验证此应用」提示，点「高级 → 继续」即可）。

## 第三步：创建桌面端 OAuth 客户端并下载 JSON

1. 左侧菜单进入 **「客户端（或凭据）」→「+ 创建客户端 / 创建凭据 → OAuth 客户端 ID」**。
2. 应用类型选择 **「桌面应用（Desktop app）」**，名称填 `LumeDesk Desktop`，点击 **「创建」**。
3. 点击 **「下载 JSON」**，保存到本地（如 `client_secret_xxx.json`）。

> 💡 **多台电脑复用**：这份 `client_secret_xxx.json` 可以在你的多台个人电脑上重复导入使用，但**切勿上传到公开 GitHub 仓库**。

## 第四步：在 LumeDesk 中一键授权

1. 点击 LumeDesk 收件箱小部件右上角的 **「配置」**。
2. 切换到 **「Google OAuth (推荐)」** 标签页。
3. 点击 **「导入客户端 JSON」**，选择刚刚下载的文件（Client ID 与 Client Secret 会自动填入）。
4. 点击 **「浏览器一键授权 (Gmail)」**，在弹出的页面选择你的 Gmail 账号：
   - 若提示「Google 尚未验证此应用」，点击左下角 **「高级」→「转至 LumeDesk（不安全）」**；
   - 勾选只读权限并点击 **「继续」**。
5. 看到网页提示授权成功后即可关闭标签页，LumeDesk 会自动拉取未读数与最新邮件列表（点击任意邮件可直接跳转到对应 Gmail 会话）。

---

## 常见问题

- **Q：一直转圈或提示连接超时？**
  - A：如所在网络环境需要代理访问 Google，请确认系统代理已正常开启；LumeDesk 会自动遵循 Windows 系统代理设置。
- **Q：安全吗？会删除或修改我的邮件吗？**
  - A：LumeDesk 仅申请最小权限 `https://www.googleapis.com/auth/gmail.readonly`（纯只读），无法发信、删信或修改邮件；Refresh Token 和 Client Secret 均通过当前 Windows 用户的 **DPAPI (`ProtectedData`)** 加密后写入本地 `config.json`，换机器或换系统账号无法解密。
