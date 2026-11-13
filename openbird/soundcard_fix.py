"""
OpenBird - Runtime fix for a memory bug in the `soundcard` library (seen in 0.4.6).

soundcard declares the Windows PROPVARIANT structure as 16 bytes, but on 64-bit
Windows it is 24 bytes. Every read of a device's `.name` or `.channels` makes
Windows write 24 bytes into a 16-byte allocation. That overrun corrupts the
process heap and Windows then terminates the app (fatal exception 0xC0000374),
usually a moment after audio devices were listed.

The fix replaces the allocation with a correctly sized, zeroed block and frees
it properly. It changes nothing on disk and is skipped if a future soundcard
version declares the structure correctly.

Import this module before any other use of soundcard.
"""

import sys
import logging

logger = logging.getLogger("openbird.soundcard_fix")

_PROPVARIANT_BYTES = 32  # real size is 24 on 64-bit Windows; 32 leaves headroom

applied = False


def apply(mf=None) -> bool:
    """Patches soundcard's _PropVariant. Returns True when the patch is active."""
    global applied
    if applied:
        return True
    try:
        if mf is None:
            if sys.platform != "win32":
                return False
            import soundcard.mediafoundation as mf  # noqa: F811

        ffi, ole32 = mf._ffi, mf._ole32
        declared = ffi.sizeof("PROPVARIANT")
        if declared >= 24:
            logger.info(f"soundcard PROPVARIANT is {declared} bytes; no fix needed.")
            return False

        def _init(self):
            raw = ole32.CoTaskMemAlloc(_PROPVARIANT_BYTES)
            if raw == ffi.NULL:
                raise MemoryError("CoTaskMemAlloc failed")
            ffi.buffer(ffi.cast("char *", raw), _PROPVARIANT_BYTES)[:] = b"\x00" * _PROPVARIANT_BYTES
            self.ptr = ffi.cast("PROPVARIANT *", raw)

        def _del(self):
            ptr = getattr(self, "ptr", None)
            if ptr is None or ptr == ffi.NULL:
                return
            self.ptr = ffi.NULL
            try:
                ole32.PropVariantClear(ptr)
            finally:
                ole32.CoTaskMemFree(ptr)

        mf._PropVariant.__init__ = _init
        mf._PropVariant.__del__ = _del
        applied = True
        logger.info(f"Applied soundcard PROPVARIANT fix (library declares {declared} bytes, Windows writes 24).")
        return True
    except Exception as e:
        logger.error(f"Could not apply the soundcard memory fix; audio device listing may crash the app: {e}")
        return False


apply()
