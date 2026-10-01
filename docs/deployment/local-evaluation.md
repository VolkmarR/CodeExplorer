# Evaluating CodeExplorer on one Windows PC

A way to try CodeExplorer from Claude Desktop without a server: unzip one folder on your own PC,
double-click a script, point Claude Desktop at a project, and ask it about the code. Nothing to
install besides Claude Desktop, and nothing reachable from any other computer.

It is for evaluation only. The server runs **without authentication**, which is safe here for one
reason only: it answers nobody but programs on this PC. Do not run it on a shared machine, and do not
make it reachable from a network. When CodeExplorer is to be used by a team, it runs on a real server
with sign-in, as [`iis-windows-server.md`](iis-windows-server.md) and
[`azure-container-apps.md`](azure-container-apps.md) describe.

The package is the same server as those two deployments, published with its own .NET runtime, plus
one small program, `CodeExplorer.McpProxy.exe`, that connects Claude Desktop to it. Nothing here is
covered by the tests beyond that program's own; treat section 9 as part of the setup.

## Before you start

- **Windows 10 or 11, 64-bit (x64).** Not ARM.
- **[Claude Desktop](https://claude.ai/download)**, signed in.
- **Network access to your git server** — GitHub, Azure DevOps, GitLab, whatever holds the code —
  over https, the way your browser reaches it.
- **A personal access token** for each private repository, with read access to its code. A public
  repository needs none.
- **No .NET, no git and no Node.js.** The package carries everything it runs. If you have them, they
  are not used.

You get the package as a zip, `codeexplorer-<version>-evaluation-win-x64.zip`. Someone with the
source code builds it with `.\build.ps1 --target=Package-Evaluation` (README, "Deploying it").

## 1. Unzip it to a short path

Unzip into **`C:\CodeExplorer`**, or another folder whose full path is short. Not your Downloads
folder, and not deep inside Documents.

The short path matters. CodeExplorer keeps a copy of each repository under the folder, and the git
library it uses keeps Windows' 260-character limit on a file's full path. A long folder name eats into
that, and a repository whose branch names are long then fails to refresh with "path too long". The
IIS guide works out the arithmetic in [section 4](iis-windows-server.md#writable-paths); with
`C:\CodeExplorer` there is room for branch names of nearly 190 characters.

You get:

| Path                                 | What it is                                                    |
| ------------------------------------ | ------------------------------------------------------------- |
| `start.cmd`                          | Starts the server and opens it in your browser.               |
| `stop.cmd`                           | Stops it.                                                     |
| `claude_desktop_config.example.json` | The entry to paste into Claude Desktop's configuration.       |
| `README.md`                          | This guide.                                                   |
| `app\`                               | The server, the proxy and their settings. Replaced on update. |
| `duckdb\extensions\`                 | The full-text search extension, already downloaded.           |
| `data\`                              | Appears on first start: your projects, indexes and clones.    |

If Windows marks the files as downloaded from the internet, the first start may show a SmartScreen
prompt: choose **More info**, then **Run anyway**. Your organisation may block that, in which case
ask whoever gave you the package.

## 2. Start it

Double-click **`start.cmd`**. A window titled *CodeExplorer server* opens and shows the server's log,
and your browser opens at <http://127.0.0.1:5000/>. Leave the server window open: closing it stops
the server.

You do not have to start it before using Claude Desktop — Claude Desktop starts it in the background
when it needs it (section 4). `start.cmd` is for the web UI, and it is also how you see the log when
something is wrong. If the server is already running, `start.cmd` only opens the browser.

## 3. Create a project and add a repository

A **project** is what Claude Desktop connects to: one or more repositories searched together. In the
browser:

1. Choose **New project**. Give it a **Slug** — a short name in lower case with hyphens, such as
   `my-project`; this is what you will type into Claude Desktop — and a **Display name**. Tick
   **Single repository project** if it will only ever hold one repository. Choose **Create project**.
2. On the project's page, choose **Add repository**. Give it a **Slug** of its own, its **Git URL** —
   the https address you would clone it with, such as `https://github.com/acme/platform.git` — and,
   for a private repository, your personal access token as the **Credential**. Choose **Add repository**.
3. Choose **Refresh**. CodeExplorer downloads the repository and builds its index; the page shows the
   progress. A first refresh of a large repository with a long history can take several minutes.

The token is stored encrypted (section 8) and never shown again. To change it, remove the repository
and add it again.

## 4. Connect Claude Desktop

Claude Desktop starts a local program for each MCP server in its configuration and talks to it over
its input and output. That program is `CodeExplorer.McpProxy.exe`: it passes Claude Desktop's
messages to the CodeExplorer server and the answers back, and it starts the server if it is not
running. Claude Desktop's custom connectors are not an alternative: they are reached from Anthropic's
cloud, and that cannot reach a server on your PC.

1. In Claude Desktop, open **Settings**, then **Developer**, and choose **Edit Config**. This opens
   the folder holding `claude_desktop_config.json`, normally
   `%APPDATA%\Claude\claude_desktop_config.json`. Open that file in Notepad.
2. Add an entry under `mcpServers`, as in `claude_desktop_config.example.json`. Replace `my-project`
   with your project's slug, and the path if you unzipped somewhere other than `C:\CodeExplorer`.
   Every backslash in the path is written twice:

   ```json
   {
     "mcpServers": {
       "codeexplorer-my-project": {
         "command": "C:\\CodeExplorer\\app\\CodeExplorer.McpProxy.exe",
         "args": ["my-project"]
       }
     }
   }
   ```

   If the file already has an `mcpServers` section, add the entry inside it, separated from the
   others by a comma. One entry per project: a second project is a second entry with its own name and
   its own slug, pointing at the same `CodeExplorer.McpProxy.exe`.
3. Save the file and **quit Claude Desktop completely** — closing its window may leave it running in
   the notification area — then start it again.
4. In a new chat, check that `codeexplorer-my-project` appears among the tools and connectors Claude
   Desktop offers, with its tools — `project_overview`, `grep`, `read_file`, `find_definition` and the
   others. If it does not, see Troubleshooting.

Then ask about the code. For example:

- *Give me an overview of the project: what it is written in, how it is laid out, and where work has
  been happening.*
- *Where is the customer's credit limit checked? Show me the code.*
- *Who has worked on the invoicing module recently, and what changed?*
- *Find every place that calls `SendInvoice` and explain what each one does with the result.*

Claude decides which tools to call and shows each call in the conversation.

### Other clients on the same PC

Two other clients connect to the server's own address directly, with no proxy. The server must be
running for them, so use `start.cmd` first.

- **Claude Code:**
  `claude mcp add --transport http codeexplorer-my-project http://127.0.0.1:5000/projects/my-project/mcp`
- **Visual Studio Code with GitHub Copilot Chat:** an `http` server in `.vscode/mcp.json` in a
  workspace, or in your user `mcp.json`:

  ```json
  {
    "servers": {
      "codeexplorer-my-project": {
        "type": "http",
        "url": "http://127.0.0.1:5000/projects/my-project/mcp"
      }
    }
  }
  ```

  Where to find the user file and how to start the server from the editor change between versions;
  see VS Code's MCP documentation.

### Why not ChatGPT or Microsoft 365 Copilot

They connect to MCP servers only from their own cloud, over public HTTPS, so they cannot reach a
server on your PC. Making them reach it would mean exposing this server — which has no sign-in — to
the internet, through a tunnel or an open port, and that is exactly what this setup must never do.
To try CodeExplorer with them, deploy it with authentication as the IIS or Azure guide describes.

## 5. Stop it

Double-click **`stop.cmd`**, or close the server window if `start.cmd` opened one. `stop.cmd` stops
the server however it was started; when Claude Desktop started it, there is no window to close.

While Claude Desktop is running, its next question starts the server again. Quit Claude Desktop first
to keep the server stopped.

## 6. Update

1. Run `stop.cmd`, and quit Claude Desktop.
2. Delete the `app` and `duckdb` folders, and unzip the new package over the folder. Keep `data`: it
   holds your projects, the downloaded repositories and their indexes.
3. If you had changed `app\appsettings.Evaluation.json` (section 10), make the same change again.
4. Start Claude Desktop, or `start.cmd`. Refresh each project once, so its index is rebuilt by the new
   version.

Keep the folder where it is. The key that decrypts stored tokens is tied to the folder's path, so a
package moved or unzipped elsewhere cannot read the tokens the old one stored: add those
repositories again.

## 7. Uninstall

1. Run `stop.cmd`, and remove the `codeexplorer-…` entries from `claude_desktop_config.json`.
2. Delete the folder.
3. The key ring that encrypted your tokens is kept outside it, under your Windows profile at
   `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`. Other ASP.NET Core applications you run may keep
   their keys in the same folder. If nothing else uses it, delete it too; otherwise leave it — without
   the `data` folder, the keys protect nothing of CodeExplorer's.

## 8. Security notes

- **There is no sign-in.** Anyone who can send a request to the server reads every project in it and
  can add repositories. That is why it listens only on `127.0.0.1`, and why it answers only requests
  addressed to `localhost`, `127.0.0.1` or `[::1]`, which keeps a web page in your browser from
  reaching it under another name.
- **Do not change the address to `0.0.0.0`, `*` or your PC's network address, and do not open the
  port in a firewall.** The proxy refuses to start with such an address in the settings, but the
  server cannot tell an evaluation from a deliberate deployment.
- **Stored tokens are encrypted with your Windows account's key ring** (section 7). Anyone who can run
  programs as your Windows user can decrypt them, as they could read your browser's saved passwords.
  Use tokens with read access only, and revoke them when the evaluation ends.
- Everything stays on this PC: the repositories, the indexes and the server's log. Only Claude
  Desktop sends what it reads to Anthropic, as part of your conversation, as it does with anything
  else you show it.

## 9. Check it works

1. `start.cmd` opens the browser on the CodeExplorer UI.
2. A project you refreshed shows its repositories and its overview in the UI.
3. In Claude Desktop, *Which project are you connected to?* makes Claude call `which_project`, which
   answers with the project's slug.
4. Run `stop.cmd`, then ask Claude another question about the code. It answers after a pause of a few
   seconds: the proxy started the server again.

## 10. Settings

The address and the folders are in **`app\appsettings.Evaluation.json`**, read by the server, by the
proxy and by `start.cmd`, so a change there is a change for all three:

```json
{
  "Urls": "http://127.0.0.1:5000",
  "Storage": { "DataDirectory": "..\\data" },
  "Index": { "ExtensionDirectory": "..\\duckdb\\extensions", "SearchEngine": "Fts" }
}
```

The paths are relative to the `app` folder. To use another port, change `5000` in `Urls` — keeping
`127.0.0.1` — and the port in the addresses of any Claude Code or VS Code entries; Claude Desktop's
entries need no change.

## Troubleshooting

Claude Desktop keeps a log for each MCP server: Settings, Developer, then the server's entry, or the
`mcp-server-<name>.log` files in `%APPDATA%\Claude\logs`. What the proxy says about a failure is
written there. For the server's own log, stop it and start it with `start.cmd`.

| What you see                                                     | What it usually is                                                          |
| ---------------------------------------------------------------- | --------------------------------------------------------------------------- |
| "The CodeExplorer server is not running at http://127.0.0.1:5000/" | The proxy could not start it. Run `start.cmd`: the server window shows why it stops. |
| The server window closes at once, or says the address is already in use | Another program uses port 5000. Change the port (section 10), or close that program. |
| "CodeExplorer has no project with the slug '…'"                  | The slug in `claude_desktop_config.json` does not match a project. Compare it with the slug in the UI, which is case-sensitive, then restart Claude Desktop. |
| The tools do not appear in Claude Desktop                         | The configuration file is not valid JSON — a missing comma or a single backslash in the path — or Claude Desktop was not fully quit. Its log says which. |
| A refresh fails with "path too long"                              | The folder's path is too long for a branch name in the repository. Move the package to a shorter path, such as `C:\CodeExplorer`, and add the repositories again (section 6). |
| A refresh fails to authenticate, or with 401 or 403               | The token is wrong, expired, or lacks read access to the repository. Remove the repository and add it again with a new token. |
| A refresh says the stored credential "cannot be decrypted"        | The package was moved, or the key ring under your profile was deleted (section 6 and 7). Remove the repository and add it again. |
| The server stops at start naming `Index:SearchEngine` or the `fts` extension | `duckdb\extensions` is missing or incomplete. Unzip the package again; the package was built with `--no-fts` if the folder is empty, and then the first start needs the internet. |
| SmartScreen or your antivirus blocks `CodeExplorer.exe` or `CodeExplorer.McpProxy.exe` | Section 1. Ask whoever gave you the package; it is not signed. |
