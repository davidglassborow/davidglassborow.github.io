# Blog

This blog can be generated as a static site using F# instead of Jekyll.

## Build and preview

Install the .NET SDK, then run:

```sh
dotnet fsi wip.fsx
python3 -m http.server --directory _site 8000
```

Open <http://localhost:8000/>. F# Interactive restores the Markdig and YamlDotNet NuGet packages on the first run. To use a different output directory, pass its path as an argument: `dotnet fsi wip.fsx /path/to/output`.

Add posts as Markdown files named `_posts/YYYY-MM-DD-slug.md` or `_posts/YYYY-MM-DD-slug.markdown`. YAML front matter can provide a `title` and `date`; otherwise the slug and filename date are used. Posts appear newest first and are written to `/YYYY/MM/DD/slug.html` using their front-matter date. Root Markdown files become pages, and a page's front-matter `permalink` can specify its URL. The generator also copies `files/` and `CNAME`, and renders `404.html` when present.

The output assumes the site is hosted at the domain root. The generator does not implement Jekyll templates, plugins or feeds, and it does not remove old output files; remove obsolete generated files before deploying. Deploy the contents of `_site` to a static web host.

## Old Jekyll run

- Install requirements: `bundle`
- `rvm use ruby-2.2.3@blog`
- `cd site`
- `bundle`
- Run local server: `jekyll serve`
- Add posts inside _posts
- Deploy using: `JEKYLL_ENV=production jekyll build; octopress deploy`

To update:

- bundle update github-pages
- bundle update inside the site

Preview:

- In site folder `bundle exec jekyll serve`

```gemfile
source 'https://rubygems.org'
gem 'github-pages', group: :jekyll_plugins
```