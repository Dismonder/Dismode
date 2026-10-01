# Dismode Memory Optimizer {{PRODUCT_VERSION}} — informacja o pochodzeniu

SPDX-License-Identifier: GPL-3.0-only

Dismode Memory Optimizer is derived from **Windows Memory Cleaner 3.0.8
© Igor Mundstein**.

- Upstream project: <https://github.com/IgorMundstein/WinMemoryCleaner>
- Upstream version used: `3.0.8`
- Upstream license declaration: `GPL-3.0`
- License for this modified component: `GPL-3.0-only`

The preserved memory-area numeric flags and the Windows memory-operation design
originate in Windows Memory Cleaner. The ported implementation is marked in the
source headers. Dismode contributors changed the implementation substantially;
see [MODIFICATIONS.md](MODIFICATIONS.md).

The Dismode installer is an aggregate. `Dismode.MemoryOptimizer`,
`Dismode.MemoryService`, and `Dismode.MemoryOptimizer.Core` form the GPL
program. They do not link to the separately distributed Dismode gaming
libraries.

Every binary release includes:

- `LICENSE` with the GNU GPL version 3 text;
- this notice and `MODIFICATIONS.md`;
- `Dismode.MemoryOptimizer-{{PRODUCT_VERSION}}-source.zip`, the corresponding source used for
  that build;
- `Dismode.MemoryOptimizer-{{PRODUCT_VERSION}}-source.zip.sha256`, containing its SHA-256;
- the upstream source URL above.

There is no warranty. Memory-list operations can cause data to be read back from
storage and can reduce responsiveness. They are not advertised as increasing FPS.
