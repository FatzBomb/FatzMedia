# User documentation site

The developer guide (`articles/`) and the API reference (generated from the public projects'
XML comments into `api/`) build into a static HTML site in `_site/` with
[DocFX](https://dotnet.github.io/docfx/), a local dotnet tool (`dotnet-tools.json`).

- Build: `scripts/build-docs.ps1` (Windows) or `scripts/build-docs.sh`. Warnings fail the build.
- Browse offline: add `-Serve` / `--serve`, then open http://localhost:8080. Browsers block the
  site's navigation, search and diagrams when pages are opened from disk (`file://`).
- Publish: upload `_site/` to any static web host (GitHub Pages, a web server, a CDN).

Writing rules: one folder per guide section with its own `toc.yml`; the order of sections is in
`articles/toc.yml`. Every API name, default and behaviour is checked against the code, and code
examples are compiled before they are published. Link API types with `<xref:Namespace.Type>`
and articles with relative paths. `_site/` and `api/` are generated and not committed.
