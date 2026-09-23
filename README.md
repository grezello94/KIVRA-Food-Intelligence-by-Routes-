# KIVRA Food Intelligence by Routes

Kitchen food-identification and expiry-label system. It intentionally contains no POS, ordering, billing, KOT, or table-management functionality.

## First vertical slice

`POST /api/labels/print` creates an immutable label snapshot and a print attempt under an idempotency key, calculates expiry in the restaurant time zone, emits configurable TSPL, sends it through a configured printer abstraction, and persists the result. `GET /api/labels` is the initial history view.

PostgreSQL is the default database. The `Fake` printer remains the safe development default and retains generated TSPL in `PrintJob.Payload` without network access.

## Run

Copy `.env.example` to `.env`, replace every placeholder with deployment-specific values, then start the full stack with `docker compose up --build`. The `.env` file is ignored by Git. The API automatically applies the committed PostgreSQL schema migration at startup. For a locally run API, set `ConnectionStrings__Default` and `Bootstrap__AdminPin` in your shell or secret store before running `dotnet run --project src/Kivra.Api`.

## Operations

- The web UI serves as an installable PWA and talks only to the local API server; Android devices never connect directly to the printer.
- Set printer `Driver` to `Fake` for safe development, or `TscTsplNetwork` for raw TCP TSPL. IP address, port, DPI, and label dimensions are saved per printer.
- `POST /api/printers/{id}/test-connection` and `/test-print` are administrator operations. A failed print creates a failed print job; it never claims a successful label print.
- The expiry worker runs every five minutes and changes active past-due labels to `Expired`, preserving history and emitting an internal notification.
- The initial administrator PIN is supplied only through `BOOTSTRAP_ADMIN_PIN` / `Bootstrap__AdminPin`; no default PIN is embedded in the application.

## Real printer deployment

- **TSC TE210 resolution is fixed at 203 DPI.** Use 300 DPI only with a TE300/TE310-class printer. KIVRA scales TSPL coordinates and built-in font choices for the configured physical resolution.
- Label width, height, gap/black-mark mode, gap size, print speed, density, and direct-thermal/thermal-transfer mode are emitted in each TSPL job. Calibrate the printer sensor after changing stock, then use **Test Print** before enabling kitchen use.
- Network printing uses raw TSPL over TCP, normally port `9100`. **Scan LAN** checks the API server interfaces, the connecting user's private `/24`, and `PRINTER_DISCOVERY_NETWORKS`. For Docker Desktop, set this variable to the restaurant LAN, for example `192.168.1.0/24`, because the container normally sees only its virtual network. Reserve the selected printer IP in DHCP.
- USB printing is server-side, not browser-side. The TSC must be installed as a RAW Windows printer queue when KIVRA runs natively on Windows, or as a CUPS raw queue when KIVRA runs on Linux/macOS. A Docker container cannot see host USB queues unless CUPS is installed in the image and the host CUPS service/socket is explicitly exposed to it. For a directly attached USB printer, running KIVRA natively on the printer host is the supported default.
- A successful TCP connection or visible USB queue only proves transport availability. Always run **Test Print** with the exact installed roll and confirm alignment before operational printing.

## Database migration workflow

The initial PostgreSQL migration is committed in `src/Kivra.Infrastructure/Migrations` and runs automatically on startup. For future schema changes, create and commit an additional migration:

```sh
dotnet tool install --global dotnet-ef
ConnectionStrings__Default='Host=localhost;Port=5432;Database=your_database;Username=your_user;Password=your_password' dotnet ef migrations add MeaningfulName --project src/Kivra.Infrastructure --startup-project src/Kivra.Api
```

For controlled production releases, run `dotnet ef database update --project src/Kivra.Infrastructure --startup-project src/Kivra.Api` before deploying and set `Database__Provider=Postgres`. SQLite remains available only for local compatibility by setting `Database__Provider=Sqlite` and a SQLite connection string.
