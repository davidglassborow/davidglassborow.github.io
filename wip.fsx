#r "nuget: Markdig, 1.5.0"
#r "nuget: YamlDotNet, 18.0.0"

open System
open System.Globalization
open System.IO
open System.Net
open System.Text.RegularExpressions
open Markdig
open YamlDotNet.RepresentationModel

let root = __SOURCE_DIRECTORY__
let output =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [||] -> Path.Combine(root, "_site")
    | [| path |] -> Path.GetFullPath(path)
    | _ -> failwith "Usage: dotnet fsi wip.fsx [output-directory]"

let isInside (directory: string) (path: string) =
    let relative = Path.GetRelativePath(directory, path)
    relative = "." || (not (Path.IsPathRooted(relative)) && relative <> ".." && not (relative.StartsWith(".." + string Path.DirectorySeparatorChar)))

if output = Path.GetFullPath(root) || isInside (Path.Combine(root, "_posts")) output || isInside (Path.Combine(root, "files")) output then
    failwith "The output directory must not be the site root or inside _posts or files"

let pipeline = MarkdownPipelineBuilder().UseAdvancedExtensions().Build()
let encode (value: string) = WebUtility.HtmlEncode(value)

let readYaml (text: string) =
    let yaml = YamlStream()
    use reader = new StringReader(text)
    yaml.Load(reader)
    if yaml.Documents.Count = 0 then Map.empty
    else
        match yaml.Documents.[0].RootNode with
        | :? YamlMappingNode as mapping ->
            mapping.Children
            |> Seq.choose (fun pair ->
                match pair.Key, pair.Value with
                | (:? YamlScalarNode as key), (:? YamlScalarNode as value) ->
                    Some (key.Value, value.Value)
                | _ -> None)
            |> Map.ofSeq
        | _ -> failwith "Expected a YAML mapping"

let readDocument path =
    let text = File.ReadAllText(path).Replace("\r\n", "\n")
    if text.StartsWith("---\n") then
        let ending = Regex.Match(text, "(?m)^---[ \\t]*$")
        let closing = ending.NextMatch()
        if not closing.Success then failwithf "Unterminated front matter in %s" path
        let metadata = text.Substring(4, closing.Index - 4) |> readYaml
        let body = text.Substring(closing.Index + closing.Length).TrimStart('\n')
        metadata, body
    else Map.empty, text

let value key fallback metadata =
    metadata |> Map.tryFind key |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue fallback

let configPath = Path.Combine(root, "_config.yml")
let config = if File.Exists(configPath) then File.ReadAllText(configPath) |> readYaml else Map.empty
let siteTitle = value "title" "Blog" config
let description = value "description" "" config

let outputPath (relativePath: string) =
    let path = Path.GetFullPath(Path.Combine(output, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)))
    let prefix = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
    if not (path.StartsWith(prefix, StringComparison.Ordinal)) then
        failwithf "Output path must be inside the output directory: %s" relativePath
    path

let write relativePath (content: string) =
    let path = outputPath relativePath
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, content)

let page title content =
    let pageTitle = if title = siteTitle then encode siteTitle else $"{encode title} | {encode siteTitle}"
    $"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="description" content="{encode description}">
  <title>{pageTitle}</title>
  <link rel="stylesheet" href="/site.css">
</head>
<body>
  <header class="site-header"><div class="wrapper"><a class="site-title" href="/">{encode siteTitle}</a><nav><a href="/about/">About</a></nav></div></header>
  <main class="wrapper">{content}</main>
  <footer class="wrapper">{encode description}</footer>
</body>
</html>
"""

let css = """* { box-sizing: border-box; }
body { margin: 0; color: #222; background: #fff; font: 16px/1.6 system-ui, sans-serif; }
.wrapper { max-width: 800px; margin: auto; padding: 0 24px; }
.site-header { border-bottom: 1px solid #ddd; margin-bottom: 40px; }
.site-header .wrapper { display: flex; justify-content: space-between; align-items: center; min-height: 64px; }
.site-title { color: #222; font-size: 1.4rem; text-decoration: none; }
a { color: #1756a9; }
h1, h2, h3 { line-height: 1.2; }
.post-meta, footer { color: #666; }
.post-list { list-style: none; padding: 0; }
.post-list li { margin: 0 0 24px; }
.post-list h2 { margin: 4px 0; }
img { max-width: 100%; height: auto; }
pre { overflow-x: auto; padding: 16px; background: #f3f3f3; }
code { font-family: ui-monospace, monospace; }
blockquote { border-left: 4px solid #ddd; color: #555; margin-left: 0; padding-left: 16px; }
footer { border-top: 1px solid #ddd; margin-top: 48px !important; padding-top: 20px !important; padding-bottom: 20px !important; }
"""

type Post = { Title: string; Date: DateTimeOffset; Url: string; Path: string; Body: string }

let postsDirectory = Path.Combine(root, "_posts")
let posts =
    if not (Directory.Exists(postsDirectory)) then []
    else
        Directory.EnumerateFiles(postsDirectory)
        |> Seq.filter (fun path -> [ ".md"; ".markdown" ] |> List.contains (Path.GetExtension(path).ToLowerInvariant()))
        |> Seq.map (fun path ->
            let name = Path.GetFileNameWithoutExtension(path)
            let fileName = Regex.Match(name, @"^(\d{4}-\d{2}-\d{2})-(.+)$")
            if not fileName.Success then failwithf "Expected a dated post filename: %s" path
            let metadata, body = readDocument path
            let dateText = value "date" fileName.Groups.[1].Value metadata
            let mutable date = DateTimeOffset.MinValue
            if not (DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, &date)) then
                failwithf "Invalid post date in %s: %s" path dateText
            let slug = fileName.Groups.[2].Value
            let datePath = date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)
            { Title = value "title" slug metadata
              Date = date
              Url = sprintf "/%s/%s.html" datePath (Uri.EscapeDataString(slug))
              Path = sprintf "/%s/%s.html" datePath slug
              Body = body })
        |> Seq.sortByDescending (fun post -> post.Date)
        |> Seq.toList

let dateText (date: DateTimeOffset) = date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)

for post in posts do
    let content = $"""<article><h1>{encode post.Title}</h1><p class="post-meta">{dateText post.Date}</p>{Markdown.ToHtml(post.Body, pipeline)}</article>"""
    write post.Path (page post.Title content)

let indexPath = Path.Combine(root, "index.md")
let indexBody = if File.Exists(indexPath) then readDocument indexPath |> snd else ""
let postList =
    posts
    |> List.map (fun post -> $"""<li><span class="post-meta">{dateText post.Date}</span><h2><a href="{encode post.Url}">{encode post.Title}</a></h2></li>""")
    |> String.concat "\n"
write "index.html" (page siteTitle $"""{Markdown.ToHtml(indexBody, pipeline)}<h1>Posts</h1><ul class="post-list">{postList}</ul>""")

for path in Directory.EnumerateFiles(root) do
    let extension = Path.GetExtension(path).ToLowerInvariant()
    let name = Path.GetFileNameWithoutExtension(path)
    if (extension = ".md" || extension = ".markdown") && name.ToLowerInvariant() <> "index" && name.ToLowerInvariant() <> "readme" then
        let metadata, body = readDocument path
        let title = value "title" name metadata
        let permalink = value "permalink" (name + ".html") metadata
        let destination = if permalink.EndsWith("/") then permalink + "index.html" else permalink
        write destination (page title $"""<article><h1>{encode title}</h1>{Markdown.ToHtml(body, pipeline)}</article>""")

let notFound = Path.Combine(root, "404.html")
if File.Exists(notFound) then
    let _, body = readDocument notFound
    write "404.html" (page "Page not found" body)

let assets = Path.Combine(root, "files")
if Directory.Exists(assets) then
    for source in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories) do
        let destination = outputPath ("files/" + Path.GetRelativePath(assets, source))
        Directory.CreateDirectory(Path.GetDirectoryName(destination)) |> ignore
        File.Copy(source, destination, true)

let cname = Path.Combine(root, "CNAME")
if File.Exists(cname) then File.Copy(cname, outputPath "CNAME", true)
write "site.css" css
printfn "Generated %d posts in %s" posts.Length output
