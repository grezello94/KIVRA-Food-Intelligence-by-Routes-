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

## Database migration workflow

The initial PostgreSQL migration is committed in `src/Kivra.Infrastructure/Migrations` and runs automatically on startup. For future schema changes, create and commit an additional migration:

```sh
dotnet tool install --global dotnet-ef
ConnectionStrings__Default='Host=localhost;Port=5432;Database=your_database;Username=your_user;Password=your_password' dotnet ef migrations add MeaningfulName --project src/Kivra.Infrastructure --startup-project src/Kivra.Api
```

For controlled production releases, run `dotnet ef database update --project src/Kivra.Infrastructure --startup-project src/Kivra.Api` before deploying and set `Database__Provider=Postgres`. SQLite remains available only for local compatibility by setting `Database__Provider=Sqlite` and a SQLite connection string.
