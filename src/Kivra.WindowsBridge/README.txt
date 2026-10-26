KIVRA Windows Print Bridge 1.8.0

Connect the TSC label printer by USB and install its Windows printer driver.
Extract this ZIP and open KIVRA Windows Print Bridge.exe, then open KIVRA on
this PC. The bridge detects a single supported TSC queue automatically, including
a renamed queue identified by its installed TSC printer driver.
If several label printers are installed, choose the intended queue in KIVRA's
printer settings. Allow local network access if your browser asks.

Local USB printing does not need a pairing code, command prompt, cloud bridge
token, or internet connection. The bridge starts its local connection before
contacting the server. First-time sign-in and loading kitchen data still need
internet; previously saved kitchen data and USB printing work offline.

The bridge runs in the Windows notification area and installs automatic startup
for the current Windows user. Open its menu > Open KIVRA printer settings for
setup, or Open status / Open log for connection details. Hiding the status window
keeps the bridge running. No .NET or Visual Studio install is needed.

Upgrade an existing installation:
1. Find KIVRA Print Bridge in the Windows notification area near the clock.
2. Right-click it and choose Exit.
3. Extract this ZIP and run KIVRA Windows Print Bridge.exe.

Existing printer selection, optional shared-print credentials, local access token,
and saved print receipts are retained. The trusted local website is
https://kivralabels.redlanternrestaurant.in. The connection listens only at
http://127.0.0.1:17847 and accepts that exact website origin. Other computers
cannot reach this PC's local connection over the network.

Shared printing from other devices is optional and can be connected in KIVRA.
If its saved cloud registration is rejected, only shared job polling pauses.
Local USB printing keeps working, with no automatic code-entry or setup dialog.
Reconnect shared printing from KIVRA when needed.

Local print receipts are saved before contacting the Windows spooler. Retrying
the same request recovers its receipt without printing another label. If KIVRA
shows an uncertain print after an interruption, check the physical label before
creating a new request. Printer power, media, USB connection and physical label
output must still be confirmed with Test Print; an installed Windows queue by
itself does not prove the printer is physically ready.

Successful local receipts sync to kitchen history when the website reconnects
without printing another copy. Reprints retain their original preparation and
expiry dates. Keep the bridge and browser running while printing.
