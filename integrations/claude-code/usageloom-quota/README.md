# usageloom-quota

A Claude Code mod for [UsageLoom](https://github.com/cynicism66/UsageLoom). After each turn it saves the current account's 5-hour and weekly rate limits (used percentage and reset time) to `usageloom-rate-limits.json` in the Claude Code config folder (`CLAUDE_CONFIG_DIR`, otherwise `.claude` in your user folder), in the same format as UsageLoom's status-line script.

Use it where the status-line script cannot run, such as the Code tab of the Claude desktop app, which never runs a status line command. It works in terminal sessions too; the two can be used together.

It saves only those two windows: no session, project, prompt, cost or account data, no network access, and it never blocks a session. Values are only reported for Pro and Max subscriptions.

## Install

At the prompt of a terminal Claude Code session:

```
/plugin install usageloom-quota --marketplace cynicism66/UsageLoom
```

Answer `y` to add the marketplace, then choose the user scope. From a shell:

```
claude plugin install usageloom-quota --marketplace cynicism66/UsageLoom
```

Installed at the user scope, it also runs in the sessions the Claude desktop app starts. To remove it: `claude plugin uninstall usageloom-quota`.
