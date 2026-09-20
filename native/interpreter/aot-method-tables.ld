/* Mono ARM64 AOT encodes method addresses as link-time CALL26 displacements.
 * Placing these tables after all AOT metadata exceeds their +/-128 MiB range
 * for Terraria. Only AOT input tables move; code remains RX and tables remain R.
 * The build verifier rejects any table relocation other than R_AARCH64_CALL26.
 */
SECTIONS
{
    .mono_aot_method_tables (READONLY) : ALIGN(8)
    {
        *.dll.o(.data.rel.ro)
        *.exe.o(.data.rel.ro)
    } :rodata
}
INSERT BEFORE .rodata;
