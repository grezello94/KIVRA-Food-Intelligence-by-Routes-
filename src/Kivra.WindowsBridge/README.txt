KIVRA Windows Print Bridge 1.7.0

Upgrade an existing installation:
1. Find KIVRA Print Bridge in the Windows notification area near the clock.
2. Right-click it and choose Exit.
3. Extract this ZIP and run KIVRA Windows Print Bridge.exe.

Existing pairing and USB queue settings are retained. The bridge installs this
version for automatic startup at the next Windows sign-in and keeps running
when its status window is hidden. No .NET or Visual Studio install is needed.

First-time setup needs an internet connection. Install the Windows USB label
printer, then select its queue and enter a pairing code from KIVRA Settings >
Print Bridge. Keep that configured queue selected for offline use.

Enable offline USB printing on the printer PC:
1. Sign in to the updated KIVRA website while online and let its offline setup
   finish. Reopen all KIVRA windows after upgrading the website.
2. Set the label printer's driver to TSC TSPL USB (TscTsplUsb).
3. In Print Bridge settings, connect the local Windows printer and allow local
   network access when your browser asks. Use this same browser on this PC.
4. Keep the bridge running. Cached kitchen data and the connected USB printer
   can then be used when the internet connection is unavailable.

The local connection listens only at http://127.0.0.1:17847 and accepts the
paired KIVRA website. Other devices cannot reach it over the network. Local
printing supports new TSC TSPL USB labels and reprints whose full original label
snapshot has been saved on this PC. Reprints keep the original dates. Other
printer drivers still need their normal server connection.

Local print receipts are saved before contacting the Windows spooler. Retrying
the same request recovers its receipt without printing another label. If KIVRA
shows an uncertain print after an interrupted bridge, check the physical label
before creating a new print request. Printed means the Windows spooler accepted
the job; printer power, media and USB output still need to be checked normally.

Successful local receipts sync to kitchen history when the website reconnects.
The bridge also continues to poll for normal cloud jobs every 250 ms while idle.
Open its notification-area menu > Open log for connection or printing details.
