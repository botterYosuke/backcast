"""Tachibana v4r9 public-key auth crypto helpers (issue #92 / ADR-0023).

v4r9 のログイン応答は仮想 URL 5 本を **RSA 公開鍵で暗号化**して返す。クライアントは
登録済み公開鍵と対の **秘密鍵で復号**する（本人性 = 「秘密鍵で復号できること」）。
復号は立花プロトコル固有の処理なので Python 側に集約する（architecture.md §1 / R3）。

一次ソース: `.claude/skills/tachibana/samples/e_api_login_pubkey.py/e_api_login_pubkey.py`
の `decrypt_sUrl` / `load_api_credentials`、references/pubkey_auth.md。

不変条件:
- パディングは **OAEP**、内部ハッシュは **SHA-256**（サーバ暗号化と一致必須）。
- base64 デコード前に `strip().replace('"', '')` でクレンジング（応答 JSON の引用符・空白）。
- 復号後は **`utf-8-sig`**（BOM 対応）でデコードし strip。
- 秘密鍵オブジェクト・PEM テキスト・復号後 URL はセッション秘密。repr / log しない（R10）。
"""
from __future__ import annotations

import base64
from pathlib import Path
from typing import Any

from .tachibana_auth import TachibanaError

__all__ = [
    "PubkeyCryptoError",
    "load_private_key",
    "load_private_key_from_file",
    "decrypt_s_url",
]


class PubkeyCryptoError(TachibanaError):
    """Raised when private-key import or sUrl decryption fails.

    Rooted under ``TachibanaError`` so callers / the login banner layer can
    catch the whole venue error tree. The message never embeds key material or
    the raw ciphertext (R10).
    """


def _import_cryptodome() -> tuple[Any, Any, Any]:
    """Lazy-import pycryptodome so module import never hard-requires the dep.

    pycryptodome の import 名は ``Cryptodome`` (pycrypto と衝突しない自立パッケージ)。
    """
    try:
        from Cryptodome.Cipher import PKCS1_OAEP
        from Cryptodome.Hash import SHA256
        from Cryptodome.PublicKey import RSA
    except ImportError as exc:  # pragma: no cover - dep wiring guard
        raise PubkeyCryptoError(
            "pycryptodome (import name 'Cryptodome') is required for v4r9 "
            "public-key auth; add it to python/pyproject.toml"
        ) from exc
    return PKCS1_OAEP, SHA256, RSA


def load_private_key(pem_text: str) -> Any:
    """Import an RSA private key from PEM text into a key object.

    `pem_text` は `utf-8-sig`+strip 済みであることを呼び出し側で保証する
    (`tachibana_credentials`)。失敗は鍵素材を含まない `PubkeyCryptoError` に翻訳する。
    """
    _, _, RSA = _import_cryptodome()
    try:
        return RSA.import_key(pem_text)
    except (ValueError, IndexError, TypeError) as exc:
        # ValueError/IndexError/TypeError = malformed PEM. Surface a clean error
        # WITHOUT the key bytes (R10).
        raise PubkeyCryptoError("failed to import RSA private key (malformed PEM?)") from exc


def load_private_key_from_file(path: str | Path) -> Any:
    """Read a PEM file (`utf-8-sig`+strip) and import the RSA private key.

    Single home for the BOM-handling + strip + import sequence so the dev-env
    resolver and the tkinter dialog don't hand-copy (and drift on) it.
    Translates missing-file / read failures to `PubkeyCryptoError` without
    leaking the path contents (R10).
    """
    pem_file = Path(path)
    if not pem_file.exists():
        raise PubkeyCryptoError(f"private key file not found: {pem_file}")
    try:
        pem_text = pem_file.read_text(encoding="utf-8-sig").strip()
    except OSError as exc:
        raise PubkeyCryptoError(f"failed to read private key file: {pem_file}") from exc
    return load_private_key(pem_text)


def decrypt_s_url(encoded_encrypted: str, private_key_obj: Any) -> str:
    """Decrypt one RSA-encrypted virtual URL with the private key.

    base64(strip+dequote) → PKCS1_OAEP(SHA256) decrypt → utf-8-sig decode → strip.
    Mirrors the official sample's ``decrypt_sUrl``.
    """
    PKCS1_OAEP, SHA256, _ = _import_cryptodome()
    decryptor = PKCS1_OAEP.new(private_key_obj, hashAlgo=SHA256)
    try:
        clean = encoded_encrypted.strip().replace('"', "")
        decoded = base64.b64decode(clean)
        plaintext = decryptor.decrypt(decoded)
    except (ValueError, TypeError) as exc:
        # base64 / OAEP failure. Never log the ciphertext or plaintext (R10).
        raise PubkeyCryptoError("failed to decrypt encrypted virtual URL") from exc
    return plaintext.decode("utf-8-sig").strip()
