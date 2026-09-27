"""Fixed stdin JSON filesystem API; every opened component rejects symlinks."""
import base64
import json
import os
import stat
import sys
import uuid

LIMIT = 256 * 1024
DIRECTORY_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW


def main():
    request = json.loads(sys.stdin.buffer.read(15 * 1024 * 1024))
    path = request["path"]
    if not isinstance(path, str) or "\x00" in path or ".." in path.split("/"):
        raise ValueError("Invalid workspace path")
    parts = path.split("/")
    if parts[:2] != ["", "workspace"]:
        raise ValueError("Path must be inside /workspace")
    parts = [part for part in parts[2:] if part and part != "."]
    operation = request["operation"]
    directory = os.open("/workspace", DIRECTORY_FLAGS)
    try:
        parents = parts if operation in ("list", "mkdir") else parts[:-1]
        for part in parents:
            if operation == "mkdir":
                try:
                    os.mkdir(part, mode=0o700, dir_fd=directory)
                except FileExistsError:
                    pass
            child = os.open(part, DIRECTORY_FLAGS, dir_fd=directory)
            os.close(directory)
            directory = child
        result = {"path": "/workspace" + ("/" + "/".join(parts) if parts else "")}
        if operation == "list":
            entries = []
            with os.scandir(directory) as iterator:
                for entry in iterator:
                    if len(entries) >= 1000:
                        raise ValueError("Directory exceeds 1000 entries")
                    info = entry.stat(follow_symlinks=False)
                    kind = "symlink" if stat.S_ISLNK(info.st_mode) else "directory" if stat.S_ISDIR(info.st_mode) else "file"
                    entries.append({"name": entry.name, "path": result["path"] + "/" + entry.name, "type": kind, "size": info.st_size})
            result["entries"] = sorted(entries, key=lambda entry: (entry["type"] != "directory", entry["name"]))
        elif operation in ("read", "read_pdf"):
            if not parts:
                raise ValueError("Choose a file")
            fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
            with os.fdopen(fd, "rb") as source:
                if not stat.S_ISREG(os.fstat(source.fileno()).st_mode):
                    raise ValueError("Only regular files can be read")
                limit = 10 * 1024 * 1024 if operation == "read_pdf" else LIMIT
                content = source.read(limit + 1)
                if len(content) > limit:
                    raise ValueError("File exceeds size limit")
            if operation == "read_pdf":
                if not content.startswith(b"%PDF-"):
                    raise ValueError("Choose a PDF file")
                result["base64"] = base64.b64encode(content).decode("ascii")
            else:
                result["text"] = content.decode("utf-8", errors="strict")
        elif operation in ("write", "write_pdf"):
            if not parts:
                raise ValueError("Choose a file")
            content = base64.b64decode(request["base64"], validate=True) if operation == "write_pdf" else request["text"].encode("utf-8")
            limit = 10 * 1024 * 1024 if operation == "write_pdf" else LIMIT
            if operation == "write_pdf" and not content.startswith(b"%PDF-"):
                raise ValueError("Choose a PDF file")
            if len(content) > limit:
                raise ValueError("File exceeds size limit")
            # Refuse symlinks and special files even when atomically replacing.
            try:
                info = os.stat(parts[-1], dir_fd=directory, follow_symlinks=False)
                if not stat.S_ISREG(info.st_mode):
                    raise ValueError("Only regular files can be replaced")
            except FileNotFoundError:
                pass
            temporary = ".agent-" + uuid.uuid4().hex
            fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
            try:
                with os.fdopen(fd, "wb") as target:
                    target.write(content)
                    target.flush()
                    os.fsync(target.fileno())
                os.replace(temporary, parts[-1], src_dir_fd=directory, dst_dir_fd=directory)
            finally:
                try:
                    os.unlink(temporary, dir_fd=directory)
                except FileNotFoundError:
                    pass
        elif operation != "mkdir":
            raise ValueError("Unsupported file operation")
        print(json.dumps(result))
    finally:
        os.close(directory)


try:
    main()
except (OSError, ValueError, KeyError, TypeError) as error:
    print(str(error), file=sys.stderr)
    sys.exit(1)
