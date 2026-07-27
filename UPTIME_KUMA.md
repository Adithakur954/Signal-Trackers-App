# Uptime Kuma Setup

This project now exposes a public health endpoint at:

- `/healthz`
- `/healthz/live`
- `/healthz/ready`

Use `/healthz/live` to check whether the backend app itself is up.
Use `/healthz/ready` to check the app plus both MySQL databases.
`/healthz` is kept as an alias for the ready check.

## Install Uptime Kuma Without Docker

Official Uptime Kuma docs support a non-Docker install. You need:

- Node.js 20.4 or newer
- Git
- PM2 if you want it to run in the background

Basic setup:

```bash
git clone https://github.com/louislam/uptime-kuma.git
cd uptime-kuma
npm run setup

# Option 1: quick test
node server/server.js

# Option 2: run in the background
npm install pm2 -g
pm2 start server/server.js --name uptime-kuma
```

Then open:

- `http://localhost:3001`

If you are on Windows, Uptime Kuma also documents a portable option on the official install page.

## What To Do In The Uptime Kuma Website

1. Open the Uptime Kuma dashboard.
2. Click `Add New Monitor`.
3. Create a monitor for the backend itself:
   - Type: `HTTP(s)`
   - URL: `https://your-domain.com/healthz/live`
   - Expected status code: `200`
4. Create a second monitor for database readiness:
   - Type: `HTTP(s)`
   - URL: `https://your-domain.com/healthz/ready`
   - Expected status code: `200`
5. Open `Notifications`.
6. Add an `Email (SMTP)` notification.
7. Fill in your SMTP host, port, username, password, from address, and a fixed recipient email or alert mailbox.
8. Save the notification and click `Test`.
9. Attach that notification to both monitors.

## Recommended Extra Checks

- Add a third monitor for your public homepage if you want a UI availability check too.
- If your app is behind a reverse proxy, monitor the public HTTPS URL instead of the internal port.
- If you want multiple people alerted, use a mailing list or distribution mailbox, or create separate notification entries for each address.

## Quick Test

You can verify the endpoint manually with:

```bash
curl -i https://your-domain.com/healthz/ready
```

You should get a `200 OK` response when the app is healthy.

## Important Note About Email Alerts

Uptime Kuma sends alerts to the email addresses you configure in its notification settings.
It does not automatically look up your app users from SignalTracker.

If you want the alert email to go to specific fixed addresses, put those addresses in the Kuma recipient field or use a distribution mailbox.
If you later want per-user or database-driven alert routing, we can add a webhook endpoint in this project and send mail from SignalTracker itself.

## What Changed In This Project

- Added a dedicated anonymous health endpoint.
- Registered `IMemoryCache` in `Program.cs` so the existing controllers can resolve correctly.
- Added separate live and ready checks for cleaner Uptime Kuma monitoring.
