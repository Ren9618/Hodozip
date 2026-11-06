use std::path::PathBuf;
use std::time::Instant;

use cozip::{CoZip, CoZipOptions, ZipDeflateMode, ZipOptions};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: hodozip-gpu-spike <archive> <output-dir> [--cpu|--hybrid]");
        std::process::exit(2);
    }

    let archive: PathBuf = args[1].clone().into();
    let output: PathBuf = args[2].clone().into();
    std::fs::create_dir_all(&output).expect("create output dir");

    // cozip's ZIP deflate decompression is CPU-only. --hybrid still initializes
    // the GPU backend (as the desktop app does), so the wall-clock cost of that
    // initialization is included; --cpu skips it.
    let cpu_only = args.iter().any(|arg| arg == "--cpu");
    let mode = if cpu_only {
        ZipDeflateMode::Cpu
    } else {
        ZipDeflateMode::Hybrid
    };

    let options = CoZipOptions::Zip {
        options: ZipOptions {
            deflate_mode: mode,
            parallel_read_threads: std::thread::available_parallelism()
                .map(|value| value.get())
                .unwrap_or(1),
            parallel_write_threads: std::thread::available_parallelism()
                .map(|value| value.get())
                .unwrap_or(1),
            ..ZipOptions::default()
        },
    };

    let start = Instant::now();
    let cozip = CoZip::init(options).expect("init cozip");
    let init = start.elapsed();
    let extract_start = Instant::now();
    let stats = cozip
        .decompress_auto_from_name(&archive, &output)
        .expect("decompress");
    let elapsed = extract_start.elapsed();

    println!(
        "mode={} init_ms={:.1} extract_ms={:.1} entries={} input_bytes={} output_bytes={}",
        if cpu_only { "cpu" } else { "hybrid" },
        init.as_secs_f64() * 1000.0,
        elapsed.as_secs_f64() * 1000.0,
        stats.entries,
        stats.input_bytes,
        stats.output_bytes
    );
}
