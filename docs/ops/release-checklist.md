# Release checklist

- [ ] CI green on Windows and Ubuntu (build, tests, coverage gate 80% line / 70% branch, vulnerability scan, CodeQL, secret scan)
- [ ] Performance job green (cold start under 10 s, log call p95 under 1 ms, tx insert p95 under 20 ms, event dispatch p95 under 5 ms)
- [ ] Performance numbers re-measured on the reference ATM PC (CI numbers are indicative only)
- [ ] Test apps run on a clean machine without .NET installed
- [ ] `atmcfg verify` passes for the release config with `--env Prod`
- [ ] Migration applied to a copy of the previous version's database
- [ ] Binaries signed with the bank certificate
- [ ] 72-hour soak and power-cut tests on hardware passed (not automated here)
- [ ] Threat model and PCI mapping reviewed for changes
- [ ] ADRs and OpenAPI document updated
- [ ] Tag created, artifacts attached
