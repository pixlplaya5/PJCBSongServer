# PJCBSongServer
Work-in-progress backend server for Project CloBeats.

## Installation
Installation instructions are currently being tested.
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
Default: C:\Users\(your username)\AppData\Local\PJCBSongServer_data
Data directory: 

Administrator email address: admin@example.com

Server listening address
Default: http://127.0.0.1:5080
For internet hosting, prefer a reverse proxy with HTTPS.
Example: https://pjcb.example.com:5080
Only choose a public-facing bind address if you understand the network and firewall configuration.
Listen URL: 

Setup completed!
Settings: (working directory)\settings.json
Data:     C:\Users\(your username)\AppData\Local\PJCBSongServer_data
Listen:   http://127.0.0.1:5080

Your JWT signing key was generated and saved securely as a random value in the settings file.
Keep the settings file private and back it up securely.

Loading server settings...
Settings loaded successfully.
```
Other platforms are currently being tested.
