# PJCBSongServer
Work-in-progress REST API server for _Project CloBeats_.

This server project will be used for retrieving songs uploaded in-game. 

## Installation

### Prerequisites
- ASP.NET Core 8.0 (refer to Google for installation instructions)
### Windows
1. Clone the repository
```
git clone https://github.com/pixlplaya5/PJCBSongServer.git
```
2. Change directory
```
cd PJCBSongServer
```
3. Build/Run
```
dotnet build
dotnet run
```
4. Run first-time setup
```
========================================
      Project CloBeats Server Setup
========================================

This is the first launch. Let's configure your server.

Where should song ZIPs and SQLite data be stored?
Default: C:\Users\(your Windows username)\AppData\Local\PJCBSongServer_data
Data directory: 

Allocated data storage limit
Default: 10 GiB
Allocated size in GiB:

Administrator username: admin [Make sure you change this on your own server!]

Server listening address
Default: http://127.0.0.1:5080
For internet hosting, prefer a reverse proxy with HTTPS.
Example: https://pjcb.example.com:5080
Only choose a public-facing bind address if you understand the network and firewall configuration.
Listen URL: 

Setup completed!
Settings: (working directory)\settings.json
Data:     C:\Users\(your Windows username)\AppData\Local\PJCBSongServer_data
Listen:   http://127.0.0.1:5080

Your JWT signing key was generated and saved securely as a random value in the settings file.
Keep the settings file private and back it up securely.

Loading server settings...
Settings loaded successfully.
```


## Endpoints
| HTTP Method | URI Endpoint | Permission | Usage |
| ----------- | ------------ | ---------- | ----- |
| GET | /api/health | Public | Retrieve server's health |
| GET | /api/lpRand | Public | Retrieve random post as loading phrase |
| POST | /api/auth/register | Public (rate-limited) | Register user
| POST | /api/auth/login | Public (rate-limited) | Sign in user |
| GET | /api/songs | Public | Retrieve song list |
| GET | /api/songs?search={query} | Public | Retrieve song list by search query |
| GET | /api/songs/{id} | Public | Retrieve song by its ID |
| GET | /api/songs/{id}/download | Public | Retrieve song by its ID to a .zip file |
| POST | /api/songs | Registered players | Upload .zip to song list |
| GET | /api/posts | Public | Retrieve post list |
| GET | /api/posts?search={query} | Public | Retrieve post list by search query |
| GET | /api/posts/{id} | Public | Retrieve post by its ID |
| POST | /api/posts | Registered players | Upload post |
| GET | /api/me | Registered players | Retrieve currently signed-in player |
| GET | /api/admin/songs/pending | Admin-only | Retrieve pending song list |
| POST | /api/admin/songs/{id}/publish | Admin-only | Publish song by its ID |
| DELETE | /api/admin/songs/{id} | Admin-only | Delete song by its ID |

## Examples (Powershell 7)

### Player Management

1. Register
``` powershell
$account = @{
    username = "player"
    password = "(12-Character-Minimum-Password!)"
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri http://(your server url)/api/auth/register `
    -ContentType "application/json" `
    -Body $account
```
2. Login
``` powershell
$login = @{
    username = "player"
    password = "(12-Character-Minimum-Password!)"
} | ConvertTo-Json

$auth = Invoke-RestMethod `
    -Method Post `
    -Uri http://(your server url)/api/auth/login `
    -ContentType "application/json" `
    -Body $login

$token = $auth.accessToken
```
3. Upload song as .zip
``` powershell
$headers = @{
    Authorization = "Bearer $token"
}

$form = @{
    file = Get-Item ".\YourSong.zip"
}

Invoke-RestMethod `
    -Method Post `
    -Uri http://(your server url)/api/songs `
    -Headers $headers `
    -Form $form
```
A successful upload will return a generated unique ID and a `Pending` status.
Uploads are rejected with HTTP 507 when the data directory is full or the
uploaded ZIP would exceed its configured allocation.

Your upload will not be public and only be visible to admins until one of them publishes it.

### Admin Management
1. Register admin user as set in first-time setup

``` powershell
$account = @{
    username = "(admin username set in first-time setup)"
    password = "(12-Character-Minimum-Password!)"
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri http://(your server url)/api/auth/register `
    -ContentType "application/json" `
    -Body $account
```
2. Log in as admin user
``` powershell
$login = @{
    username = "(admin username set in first-time setup)"
    password = "(12-Character-Minimum-Password!)"
} | ConvertTo-Json

$adminAuth = Invoke-RestMethod `
    -Method Post `
    -Uri http://(your server url)/api/auth/login `
    -ContentType "application/json" `
    -Body $login
```
3. Use returned Bearer token to retrieve pending songs
``` powershell
$adminHeaders = @{
    Authorization = "Bearer $($adminAuth.accessToken)"
}

Invoke-RestMethod `
    -Uri "http://(your server url)/api/admin/songs/pending" `
    -Headers $adminHeaders
```
4. Publish listed song
``` powershell
$songId = "REPLACE_WITH_UNPUBLISHED_SONG_ID"

Invoke-RestMethod `
    -Method Post `
    -Uri "http://(your server url)/api/admin/songs/$songId/publish" `
    -Headers $adminHeaders
```
