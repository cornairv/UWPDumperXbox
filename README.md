# UWPDumperXbox

A tool that injects code into UWP processes on Xbox One and Xbox Series consoles to bypass encryption and dump files (similar to [UWPDumper](https://github.com/Wunkolo/UWPDumper)).
It requires a console vulnerable to [Collateral Damage](https://github.com/exploits-forsale/collateral-damage).

## Usage

1. Download `UWPDumperXbox.xml` from [releases](https://github.com/cornairv/UWPDumperXbox/releases/latest), copy it to a USB drive, and plug it in to the Xbox
2. Run Collateral Damage and connect to the reverse shell
3. Start a background reverse shell and connect to it. A couple options:
    - The [game dumping pack](https://consolemods.org/wiki/Xbox_One:Dumping_Games_with_Durango_Dumplings_v2) includes a simple background reverse shell
	- [AnimaSSH](https://github.com/kwsimons/AnimaSSH) starts an OpenSSH server that keeps running after GameScript closes
4. Start the app you want to dump, and find its process name with `Get-Process` in powershell
5. To dump the app to the USB drive:
	- Run UWPDumperXbox: `D:\dotnet\dotnet.exe msbuild D:\UWPDumperXbox.xml /v:detailed /p:TargetName=<process> /p:Destination=D:\dumps\`
    - The app is dumped as a zip file in the destination directory
6. To dump the app to a PC over LAN:
	- Download `UploadServer.exe` from [releases](https://github.com/cornairv/UWPDumperXbox/releases/latest) and run it on PC: `.\UploadServer.exe 8000`
	- Run UWPDumperXbox: `D:\dotnet\dotnet.exe msbuild D:\UWPDumperXbox.xml /v:detailed /p:TargetName=<process> /p:Destination=http://<ip>:8000`
	- The app is dumped as a zip file in `uploads`

## Command line reference

UWPDumperXbox runs as a dotnet msbuild task to bypass code signing:

```
dotnet msbuild .\UWPDumperXbox.xml <parameters>
```

Parameters:
- `/p:TargetName=<process>`: The name of the app's process
- `/p:Destination=...`: Can be one of these:
	- `/p:Destination=<path>`: The file path to save the dump zip file as
	- `/p:Destination=<directory>`: The directory to save the dump zip file in with an automatic name
	- `/p:Destination=http://<ip>:<port>`: The IP of the PC and the port UploadServer is listening on
- `/p:Overwrite=<true|false>`: Overwrite the destination file if it already exists (default: false)
- `/p:OverrideSource=<path>`: The source directory to dump files from (default: parent directory of the app executable)
- `/p:OverridePid=<pid>`: The pid of the app to dump (default: pid of the first process matching TargetName)
- `/v:detailed`: Verbose logging (show file list and other information)

UploadServer is a simple HTTP server that receives files from UWPDumperXbox and saves them in `uploads`:

```
.\UploadServer <parameters>
```

Parameters:
- `port`: The TCP port to listen on (default: 8000)