# Quality Studio analysis package

This folder is the transitional local NuGet source for AGT-2655. It exists
because the Quality Studio analysis-core and rule-library deliveries were not
yet published to a package feed when the first Agent Studio slice was built.

- Package: `AgentOrchestrator.CodeQuality.0.1.0-agt2655.1.nupkg`
- SHA-256: `ff49a17eec931efbba00728c46791fbda27cb66b53046763a77f587eed86355f`
- Analysis-core source: QS-91 commit
  `1be25369af83c9aff6548c0382bce9b63c6dd9a0`
- Rule-library source: QS-90 commit
  `9e9461f1025afa18c6082a3f82f5649bfdcdf4e3`

The package combines those two coordinated Quality Studio source deliveries.
Agent Studio contains only the adapter and policy. It does not duplicate the
rule definitions. Replace this local source and exact prerelease pin with the
published package version once Quality Studio publishes the combined package.
