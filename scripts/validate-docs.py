import json
import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parent.parent
CONTENT = ROOT / "docs-site" / "content"
CODE_BLOCK = re.compile(r"^ {0,3}(`{3,}|~{3,})[^\n]*\n.*?^ {0,3}\1[ \t]*$", re.M | re.S)
LINK = re.compile(
    r'\]\(\s*(<[^>\n]+>|[^\s)]+)|^\[[^\]\n]+\]:[ \t]*(<[^>\n]+>|[^\s]+)'
    r'|\bhref=["\']([^"\']+)["\']',
    re.M,
)


def site_route_exists(path):
    target = CONTENT / path.lstrip("/")
    return target.is_file() or (target / "index.mdx").is_file() or (
        target / f"{target.name}.mdx"
    ).is_file()


def broken_links(file, site_host):
    text = CODE_BLOCK.sub("", file.read_text(encoding="utf-8"))
    for match in LINK.finditer(text):
        link = next(value for value in match.groups() if value is not None)
        target = urlsplit(link.removeprefix("<").removesuffix(">"))
        if target.hostname == site_host or (
            not target.netloc and target.path.startswith("/") and file.is_relative_to(CONTENT)
        ):
            exists = site_route_exists(unquote(target.path))
        elif target.scheme or target.netloc or not target.path:
            continue
        else:
            exists = (file.parent / unquote(target.path)).exists()
        if not exists:
            yield f"{file.relative_to(ROOT)}: broken link {link}"


def main():
    config = json.loads((CONTENT / "siteConfig.json").read_text(encoding="utf-8"))
    site_host = urlsplit(config["sitemap"]["baseUrl"]).hostname
    docs = sorted(path for path in CONTENT.rglob("*.mdx") if "_private" not in path.parts)
    files = [ROOT / "README.md", ROOT / "src" / "README-NUGET.md", *docs]
    errors = [error for file in files for error in broken_links(file, site_host)]
    navigation = sorted(CONTENT.rglob("_nav.json"))
    for file in navigation:
        try:
            parsed = json.loads(file.read_text(encoding="utf-8"))
            if not isinstance(parsed, (dict, list)):
                errors.append(f"{file.relative_to(ROOT)}: expected a navigation object or array")
        except json.JSONDecodeError as error:
            errors.append(f"{file.relative_to(ROOT)}: {error}")
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"Validated {len(files)} Markdown files and {len(navigation)} navigation files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
