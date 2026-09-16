# MkDocs Documentation

This directory contains the MkDocs-based documentation for VIIPER.

## Setup

Install MkDocs with Material theme:

```bash
pip install mkdocs-material
```

## Development

Run the documentation server locally:

```bash
cd doc
mkdocs serve
```

Then open http://127.0.0.1:8000/ in your browser.

## Building

Build the static documentation site:

```bash
cd doc
mkdocs build
```

The built site will be in the `site/` directory.

## Deployment

Deploy to GitHub Pages:

```bash
cd doc
mkdocs gh-deploy
```

## Documentation Structure

- `mkdocs.yml` - MkDocs configuration
- `docs/` - Documentation source files (Markdown)
    - `index.md` - Home page
    - `getting-started/` - Installation and quick start
    - `cli/` - CLI reference
    - `api/` - API reference
