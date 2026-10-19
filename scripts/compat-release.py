#!/usr/bin/env python3
"""Validate the library compatibility matrix and produce release notes.

Works on the committed ``compat/library-matrix.json`` shape (no network, no
third-party packages): JSON-schema validation against
``compat/library-matrix.schema.json``, semantic rules the schema cannot
express (server parser bounds, range tokens, real sunset dates, minimum moves
only on top of a completed deprecation), the release gate (a matching-commit
evidence row for the recommended library on the exact release commit: a
``ci-only`` row from the green Compat engine legs, or a ``pass`` row from a
native editor run), and rendering of the "Tested with" table. Preserved-data
rollback targets are attached from target-binary ``compatibility --json`` reports.
"""
import argparse
import calendar
import datetime as dt
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Mirrors ClientIdentity.TryParseLibraryVersion (Compat/ClientIdentity.cs): strict
# SemVer core, optional pre-release and build metadata. The int32 bound on each
# core part and the 64-char cap live in valid_version. Used when the schema
# carries no SemVer pattern of its own.
SEMVER_PATTERN = r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$"


def resolve_ref(schema, root):
    ref = schema["$ref"]
    if not ref.startswith("#/"):
        raise ValueError("Only local schema references are supported: " + ref)
    node = root
    for part in ref[2:].split("/"):
        node = node[part.replace("~1", "/").replace("~0", "~")]
    return node


def find_pattern(node, root, seen=None):
    """First "pattern" under $ref / anyOf / oneOf / allOf composition, else None."""
    if not isinstance(node, dict):
        return None
    if isinstance(node.get("pattern"), str):
        return node["pattern"]
    if seen is None:
        seen = set()
    ref = node.get("$ref")
    if isinstance(ref, str) and ref.startswith("#/") and ref not in seen:
        seen.add(ref)
        target = root
        try:
            for part in ref[2:].split("/"):
                target = target[part.replace("~1", "/").replace("~0", "~")]
        except (KeyError, TypeError):
            target = None
        found = find_pattern(target, root, seen)
        if found is not None:
            return found
    for keyword in ("anyOf", "oneOf", "allOf"):
        branches = node.get(keyword)
        if isinstance(branches, list):
            for branch in branches:
                found = find_pattern(branch, root, seen)
                if found is not None:
                    return found
    return None


def semver_pattern(schema):
    """SemVer pattern from the schema's minimum/recommended property, else the server parser rules."""
    properties = schema.get("properties") if isinstance(schema, dict) else None
    if isinstance(properties, dict):
        for name in ("recommended", "minimum"):
            found = find_pattern(properties.get(name), schema)
            if found is not None:
                return found
    return SEMVER_PATTERN


def check_schema(value, schema, root, path="$", errors=None):
    """Validate the subset our matrix schema uses. Unknown keywords fail, annotations pass."""
    if errors is None:
        errors = []
    if root is None:
        root = schema
    allowed = {"$schema", "$id", "title", "description", "$defs", "definitions", "$ref", "anyOf", "oneOf", "allOf", "if", "then",
               "type", "const", "enum", "pattern", "format", "properties", "required",
               "additionalProperties", "items", "minItems", "uniqueItems", "minLength", "minimum"}
    if set(schema) - allowed:
        errors.append(f"{path}: unsupported schema keywords {set(schema) - allowed}")
        return errors
    if "$ref" in schema:
        check_schema(value, resolve_ref(schema, root), root, path, errors)
    for keyword in ("anyOf", "oneOf"):
        # oneOf is checked as anyOf (at least one branch matches): the shapes in
        # use are nullable unions, whose branches can overlap, so exactly-one
        # would false-positive while at-least-one still rejects garbage.
        if keyword in schema:
            matches = [err for branch in schema[keyword] for err in [check_schema(value, branch, root, path, [])] if not err]
            if not matches:
                errors.append(f"{path}: matches none of the allowed shapes")
    if "allOf" in schema:
        for index, branch in enumerate(schema["allOf"]):
            check_schema(value, branch, root, f"{path}+{index}", errors)
    if "if" in schema and not check_schema(value, schema["if"], root, path, []):
        check_schema(value, schema.get("then", {}), root, path, errors)
    types = {"null": value is None, "object": isinstance(value, dict), "array": isinstance(value, list),
             "string": isinstance(value, str), "integer": type(value) is int, "number": type(value) in (int, float)}
    wanted = schema.get("type")
    if wanted and not any(types.get(t, False) for t in (wanted if isinstance(wanted, list) else [wanted])):
        errors.append(f"{path}: expected {wanted}")
        return errors
    if "const" in schema and value != schema["const"]:
        errors.append(f"{path}: expected {schema['const']}")
    if "enum" in schema and value not in schema["enum"]:
        errors.append(f"{path}: not in enum")
    if isinstance(value, str):
        if "pattern" in schema and not re.search(schema["pattern"], value):
            errors.append(f"{path}: invalid format")
        if len(value) < schema.get("minLength", 0):
            errors.append(f"{path}: empty string")
        if schema.get("format") == "date":
            try:
                dt.date.fromisoformat(value)
            except ValueError:
                errors.append(f"{path}: invalid date")
    if type(value) is int and value < schema.get("minimum", value):
        errors.append(f"{path}: below minimum")
    if isinstance(value, dict):
        for key in schema.get("required", []):
            if key not in value:
                errors.append(f"{path}: missing {key}")
        properties = schema.get("properties", {})
        for key, item in value.items():
            if key in properties:
                check_schema(item, properties[key], root, f"{path}.{key}", errors)
            elif schema.get("additionalProperties") is False:
                errors.append(f"{path}: unexpected {key}")
    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0):
            errors.append(f"{path}: too few items")
        if schema.get("uniqueItems") and len({json.dumps(item, sort_keys=True) for item in value}) != len(value):
            errors.append(f"{path}: duplicate items")
        for index, item in enumerate(value):
            check_schema(item, schema.get("items", {}), root, f"{path}[{index}]", errors)
    return errors


def six_months(date):
    month = date.month + 6
    year = date.year + (month - 1) // 12
    month = (month - 1) % 12 + 1
    return date.replace(year=year, month=month, day=min(date.day, calendar.monthrange(year, month)[1]))


def valid_version(value, pattern):
    if not isinstance(value, str) or len(value) > 64 or not re.fullmatch(pattern, value):
        return False
    return all(int(part) <= 2_147_483_647 for part in re.split(r"[-+]", value, maxsplit=1)[0].split("."))


def check_range(text, pattern):
    # Same tokenization as VersionRange.TryParse (src/SboxNetworkStorage.Server/Compat/VersionRange.cs):
    # split on spaces and drop empties, so the gate accepts exactly the strings the
    # server loader accepts (a double space is fine in both).
    if not isinstance(text, str) or not text.strip() or len(text) > 256:
        raise ValueError("Invalid library version range")
    for token in [token for token in text.split(" ") if token]:
        match = re.fullmatch(r"(<=|>=|<|>|=)?(.+)", token)
        if match is None or not valid_version(match.group(2), pattern):
            raise ValueError("Invalid library version range token: " + token)


def validate(matrix, schema, today, baseline=None):
    schema_errors = check_schema(matrix, schema, schema)
    # The committed schema carries the SemVer shape as $ref "#/$defs/semver" on
    # minimum/recommended, not as an inline "pattern": follow the reference
    # (or fall back to the server parser rules) instead of indexing it directly.
    semver = semver_pattern(schema)
    # Semantic messages are collected ahead of the schema errors so the specific
    # cause ("range token", "real sunset date") stays visible even when the
    # value also fails the schema shape. Defensive .get access: a matrix that
    # fails the schema shape must still report errors, not KeyError.
    semantic = []
    policy = matrix if isinstance(matrix, dict) else {}
    for name in ("minimum", "recommended"):
        if not valid_version(policy.get(name), semver):
            semantic.append(name + " must be SemVer within the server parser bounds")
    for library in policy.get("libraries") or []:
        if not isinstance(library, dict):
            continue
        if library.get("version") != "legacy" and not valid_version(library.get("version"), semver):
            semantic.append("Library release version must be SemVer or legacy")
        if library.get("minServer") is not None and not valid_version(library.get("minServer"), semver):
            semantic.append("Library minServer must be SemVer within the server parser bounds")
        if library.get("released") is not None:
            try:
                dt.date.fromisoformat(library["released"])
            except (ValueError, TypeError):
                semantic.append("Library release date must be a real date")
    for rule in policy.get("deprecated") or []:
        if not isinstance(rule, dict):
            continue
        try:
            check_range(rule.get("range"), semver)
        except ValueError as error:
            semantic.append(str(error))
        try:
            dt.date.fromisoformat(rule.get("sunset"))
        except (ValueError, TypeError):
            semantic.append(f"deprecated range '{rule.get('range')}' needs a real sunset date (yyyy-MM-dd)")
    for rule in policy.get("blocked") or []:
        if not isinstance(rule, dict):
            continue
        try:
            check_range(rule.get("range"), semver)
        except ValueError as error:
            semantic.append(str(error))
    # LTS support window (docs/compatibility.md, docs/releasing.md): a deprecated
    # library entry superseded by a newer supported entry keeps working until at
    # least six months after the successor shipped, and at least 90 days after
    # the deprecation was announced when the matrix records an announcement.
    def semver_key(version):
        try:
            core = re.split(r"[-+]", version, maxsplit=1)[0].split(".")
            prerelease = "-" in version or "+" in version
            return (int(core[0]), int(core[1]), int(core[2]), not prerelease, version)
        except (ValueError, AttributeError, IndexError, TypeError):
            return None
    libraries = [library for library in policy.get("libraries") or [] if isinstance(library, dict)]
    for library in libraries:
        if library.get("status") != "deprecated" or library.get("version") in (None, "legacy"):
            continue
        key = semver_key(library.get("version"))
        if key is None:
            continue  # already reported as a bad version above
        successors = [other for other in libraries
                      if other.get("status") == "supported" and other.get("version") != "legacy"
                      and other.get("released") is not None
                      and (semver_key(other.get("version")) or ()) > key]
        if not successors:
            continue
        successor = min(successors, key=lambda other: semver_key(other.get("version")) or ())
        try:
            released = dt.date.fromisoformat(successor["released"])
        except (ValueError, TypeError):
            continue  # already reported as a bad release date above
        try:
            sunset = dt.date.fromisoformat(library.get("sunset"))
        except (ValueError, TypeError):
            continue  # the schema shape requires a real sunset date
        if sunset < six_months(released):
            semantic.append(f"Library {library.get('version')} sunset {library.get('sunset')} must be at least six months "
                            f"after its successor {successor.get('version')} released {successor.get('released')}")
        announced = library.get("announced")
        if announced is not None:
            try:
                announced_date = dt.date.fromisoformat(announced)
            except (ValueError, TypeError):
                semantic.append(f"Library {library.get('version')} announcement date must be a real date (yyyy-MM-dd)")
                continue
            if sunset < announced_date + dt.timedelta(days=90):
                semantic.append(f"Library {library.get('version')} sunset {library.get('sunset')} must be at least 90 days "
                                f"after its announced deprecation {announced}")
    if isinstance(baseline, dict):
        old = {rule.get("range"): rule.get("sunset") for rule in baseline.get("deprecated") or [] if isinstance(rule, dict)}
        new = {rule.get("range"): rule.get("sunset") for rule in policy.get("deprecated") or [] if isinstance(rule, dict)}
        for rib, sunset in old.items():
            if rib not in new:
                semantic.append(f"Announced sunset for '{rib}' was removed; announced sunsets cannot be shortened")
                continue
            try:
                shortened = dt.date.fromisoformat(new[rib]) < dt.date.fromisoformat(sunset)
            except (ValueError, TypeError):
                shortened = False  # already reported as a bad sunset date above
            if shortened:
                semantic.append(f"Announced sunset for '{rib}' was shortened; announced sunsets cannot be shortened")
        if baseline.get("minimum") != policy.get("minimum"):
            rule = next((r for r in policy.get("deprecated") or [] if isinstance(r, dict) and r.get("range") == "<" + str(policy.get("minimum"))), None)
            try:
                completed = rule is not None and dt.date.fromisoformat(rule.get("sunset")) <= today
            except (ValueError, TypeError):
                completed = False
            if not completed:
                semantic.append("Raising the library minimum requires a completed deprecation entry for <minimum")
    errors = semantic + schema_errors
    if errors:
        raise ValueError("\n".join(errors))


def select_rows(matrix, server, commit):
    rows = [r for r in matrix["tested"] if r.get("server") == server]
    if commit:
        rows = [r for r in rows if r.get("commit") and (r["commit"].startswith(commit) or commit.startswith(r["commit"]))]
    return rows


def render(matrix, server, commit):
    rows = select_rows(matrix, server, commit)
    recommended = matrix.get("recommended")
    evidence = [r for r in rows if r.get("library") == recommended and r.get("result") in ("pass", "ci-only")]
    if not evidence:
        raise ValueError("No matching-commit evidence row for the recommended library on this exact commit; record the Compat engine legs (ci-only) or a native editor run (pass)")
    native = any(r.get("result") == "pass" for r in evidence)
    lines = ["## Tested with", "",
             "| Library | Revision | Engine | OS | Database | Result | Evidence |",
             "| --- | --- | --- | --- | --- | --- | --- |"]
    for row in rows:
        def escape(text):
            return str(text).replace("|", "\\|").replace("\n", " ")
        cells = [row.get("library"), row.get("revision") if row.get("revision") is not None else "unknown", row.get("engine") or "-",
                 row.get("os") or "-", ", ".join(row.get("database", [])), row.get("result"),
                 ", ".join(row.get("evidence", []))]
        lines.append("| " + " | ".join(escape(c) for c in cells) + " |")
    if not native:
        lines += ["", "**CI-only release: no native s&box editor run is recorded for this commit. The green Compat engine legs (real library inside the real s&box engine) are the certification.**"]
    return "\n".join(lines)


def rollback_notes(paths):
    lines = ["", "## Rollback compatibility"]
    if not paths:
        return "\n".join(lines + ["No preserved-data rollback target certified for this release. Older pre-compatibility binaries require backup restore."])
    for path in paths:
        report = json.loads(path.read_text())
        if report.get("protocol") != 1 or report.get("compatible") is not True or not report.get("version"):
            raise ValueError("Rollback reports must be successful target-binary compatibility --json results")
        floor = report.get("minBinarySchema")
        supported = report.get("supportedSchemaVersion")
        database = report.get("schemaVersion")
        if type(floor) is not int or type(supported) is not int or type(database) is not int:
            raise ValueError("Rollback report has no valid schema versions")
        if floor > supported:
            raise ValueError("Rollback target has no valid certified schema floor")
        if database < supported:
            # The target supports a newer schema than the database it was probed against. That is safe:
            # the older database migrates forward on start, so warn instead of refusing the target.
            lines.append(f"- Preserved-data rollback target: **{report['version']}**; database schema {database}, target supports newer schema {supported}, certified floor {floor}. The target migrates the older database forward on start.")
        else:
            lines.append(f"- Preserved-data rollback target: **{report['version']}**; database schema {database}, target supports {supported}, certified floor {floor}.")
    lines.append("Use `rollback --keep-data`; it rechecks the actual target and every instance before changing the binary. These reports certify schema/config compatibility, not a game-editor test.")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--matrix", type=Path, default=ROOT / "compat/library-matrix.json")
    parser.add_argument("--server")
    parser.add_argument("--commit")
    parser.add_argument("--baseline", type=Path, help="Previous release's matrix; verifies its announced sunsets are not shortened")
    parser.add_argument("--policy-source", type=Path, help="Tested source matrix; a detached artifact may change only tested evidence")
    parser.add_argument("--rollback-report", type=Path, action="append", default=[], help="Target binary compatibility --json report against the release schema/config")
    args = parser.parse_args()
    try:
        matrix = json.loads(args.matrix.read_text())
        schema = json.loads((ROOT / "compat/library-matrix.schema.json").read_text())
        baseline = json.loads(args.baseline.read_text()) if args.baseline else None
        validate(matrix, schema, dt.date.today(), baseline)
        if args.policy_source:
            source = json.loads(args.policy_source.read_text())
            if {key: value for key, value in source.items() if key != "tested"} != {key: value for key, value in matrix.items() if key != "tested"}:
                raise ValueError("Detached release evidence cannot change the tested source policy")
        output = []
        if args.server:
            output.append(render(matrix, args.server, args.commit or ""))
        output.append(rollback_notes(args.rollback_report))
        print("\n".join(line for line in output if line))
    except (ValueError, FileNotFoundError, json.JSONDecodeError) as error:
        print(f"compat-release: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
