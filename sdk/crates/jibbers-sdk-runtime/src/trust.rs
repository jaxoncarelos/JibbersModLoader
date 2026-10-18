//! Public keys that may sign SDK releases. A release is installed only if its manifest verifies
//! against one of these. To rotate: add the new key to the list in a release signed by the old key,
//! publish it, and remove the old key a release or two later.
//!
//! The matching private key never leaves the maintainer's machine (`sdk-publish keygen`).

pub const KEYS: &[[u8; 32]] = &[
    // 8f13703c186ea13de67b83a046bdd6975961367c754abaf7879a56ce690ccd9e
    [
        0x8f, 0x13, 0x70, 0x3c, 0x18, 0x6e, 0xa1, 0x3d, 0xe6, 0x7b, 0x83, 0xa0, 0x46, 0xbd, 0xd6, 0x97, 0x59, 0x61, 0x36, 0x7c, 0x75, 0x4a, 0xba, 0xf7, 0x87, 0x9a, 0x56, 0xce, 0x69, 0x0c, 0xcd, 0x9e,
    ],
];
