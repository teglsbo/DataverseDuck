# Contributing

For anything beyond a small fix, open an issue first — a lot of the design here rests
on decisions recorded in [`docs/adr/`](docs/adr/); check the relevant one before
assuming a behaviour is accidental.

## Build and test

Requires .NET 10.

```bash
dotnet restore
dotnet build --configuration Release
dotnet test
```

No live Dataverse connection is needed for `dotnet test`; that's only exercised via
`spikes/` or `dvduck doctor`.

## Commits

Git history is the changelog here — one logical change per commit, with the *why* in
the body.

## Bug reports

Include the smallest reproducing query/scenario, and whether it needs a live Dataverse
connection to show up.
