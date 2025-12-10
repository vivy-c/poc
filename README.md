# Call Transcription POC

Proof-of-concept for running Azure Communication Services group calls with transcription and post-call summaries. The frontend is a Vite + React 18 TypeScript SPA that embeds the ACS `CallComposite` and handles persona selection, call start/join, transcript polling, and a summary view. The backend is an Azure Functions isolated worker (.NET 8) that issues ACS identities/tokens, orchestrates call automation + Speech transcription, and (optionally) calls Azure OpenAI for summaries. Docker Compose is available for a local stack with Azurite.

## Project layout
- `frontend/` – React SPA with ACS CallComposite, persona selector, call workspace, transcript + summary views.
- `backend/` – Azure Functions isolated project (`CallTranscription.Functions.csproj`) for ACS identities, call lifecycle, webhooks, transcripts, and summaries.
- `docker-compose.yml` – optional local stack (frontend dev server, Functions host, Azurite).
- `CallTranscription.sln` – solution file for the backend.

## Prerequisites
- Node 18+ (20 recommended) and npm.
- .NET 8 SDK and Azure Functions Core Tools v4.
- Azure Communication Services resource (connection string).
- Azure AI Speech resource (key + endpoint or region) for transcription.
- Azure OpenAI resource (optional) for AI summaries; fallback summary is used if missing.
- Storage connection string or Azurite (Table storage is used for sessions/participants; transcript/summary are in-memory).
- Docker (optional) if using docker-compose.

## Quickstart (local dev)
1) Configure backend settings:
```sh
cd backend
cp local.settings.json.example local.settings.json
# Fill the values below
```
Required values:
- `ACS__ConnectionString` – ACS connection string.
- `Speech__Key` plus `Speech__Endpoint` (preferred) or `Speech__Region`.
- `Webhook__PublicBaseUrl` – public URL where ACS can reach `/api/call-events` (use an https tunnel locally).

Optional but recommended:
- `OpenAI__Endpoint`, `OpenAI__Key`, `OpenAI__DeploymentName` for AI summaries.
- `AzureWebJobsStorage` or `Storage__ConnectionString` for table persistence (Azarite/UseDevelopmentStorage works).
- `Webhook__Key`, `Webhook__HeaderName`, `Webhook__EnforceKey` to secure ACS webhooks.
- Feature flags: `Features__EnableTranscription`, `Features__EnableSummaries`, `Features__CleanupRetentionDays`, `Features__StaleCallMinutes`.

2) Run the backend:
```sh
cd backend
dotnet restore
func start --cors "*" --verbose
```

3) Run the frontend:
```sh
cd frontend
npm install
# VITE_API_BASE_URL defaults to http://localhost:7071/api when dev server runs on 5173
npm run dev -- --host 0.0.0.0 --port 5173
```

4) Optional: run the full stack with Docker (requires `backend/local.settings.json` to be present):
```sh
docker compose up --build
# tear down
docker compose down -v
```
The compose file boots Azurite for storage, the Functions host on `7071`, and the Vite dev server on `5173` with `VITE_API_BASE_URL=http://localhost:7071`.

## Using the demo
- Pick a demo persona (no auth; stored in localStorage).
- Optionally pre-select other demo users, then start a call. Identities/tokens are provisioned for everyone; the first invitee opens in a new window for a quick 2-user demo.
- Join an existing call by pasting a `callSessionId`; groupId/callConnectionId are shown for debugging.
- While connected, the transcript panel polls `/api/calls/{id}/transcript`. After ending the call, switch to the summary view to see `/api/calls/{id}/summary` (auto-polls until ready).
- Add more demo participants mid-call via `/api/calls/{id}/add-participant`.
- End the call to dispose the adapter and load the recap (summary, key points, action items, transcript).

## Backend endpoints
- `GET /api/health` – service status.
- `POST /api/demo-users/init` – provision/return ACS identity for a demo user.
- `POST /api/calls/start` – start a call, provision participants, return ACS token + group id.
- `POST /api/calls/{callSessionId}/join` – join an existing call as a demo user.
- `POST /api/calls/{callSessionId}/add-participant` – add more demo users to the call (ACS invite sent when possible).
- `GET /api/calls/{callSessionId}/transcript` – current transcript + call metadata.
- `GET /api/calls/{callSessionId}/summary` – call metadata + AI/fallback summary.
- `POST /api/call-events` – ACS call/cognitive events webhook; honors `Webhook__HeaderName`/`Webhook__Key` if configured.

## Notes
- ACS call automation connects server-side only when `Webhook__PublicBaseUrl` is set (required for transcription). Speech must be configured or transcription will fail.
- Call sessions/participants persist to Table Storage when a storage connection string is available; transcripts and summaries are kept in memory for this POC.
- A timer (`cleanup-stale-calls`) marks stale sessions complete every 15 minutes based on `Features__StaleCallMinutes`.
