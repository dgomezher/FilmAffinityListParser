# FilmAffinityListParser

FilmAffinityListParser is a .NET tool for parsing and processing lists from your exported archive (https://www.filmaffinity.com/es/download-my-data.php) in FilmAffinity, the popular Spanish movie database and recommendation site. It is designed to be run as part of a Docker Compose stack and integrates with Radarr and LibreTranslate for enhanced search.

## Features

- Parses FilmAffinity exported lists from a desired location.
- Outputs processed results to a configurable location.
- Easy setup with with Docker Compose.
- Integrates with Radarr for movies' lookup.
- Integrates with LibreTranslate for extending lookup's capabilities (not too much for now, Spanish translation for some films are a joke).

## Running with Docker Compose

This project is designed to be run using Docker Compose. The main service is `filmaffinitylistparser`.

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/)
- [Docker Compose](https://docs.docker.com/compose/)

### Quick Start

1. **Export your list from FilmAffinity** and place the file in a directory on your host machine.
2. **Set environment variables** for input/output paths by creating a `.env` file in the project root. Example:

    ```env
    INPUT_PATH=/absolute/path/to/input
    OUTPUT_PATH=/absolute/path/to/output
    ```

    - Place your exported FilmAffinity file inside the `INPUT_PATH` directory.

3. **Start the stack:**

    ```sh
    docker compose up
    ```

    This will start the FilmAffinityListParser, Radarr, and LibreTranslate services.

### How File Paths Work

- The parser expects the **input files** to be available at the path specified by the `INPUT_PATH` environment variable.
- **Processed results** will be written to the path specified by `OUTPUT_PATH`.
- Both paths must be defined in your `.env` file. Example:

    ```env
    INPUT_PATH=/home/user/filmaffinity/input
    OUTPUT_PATH=/home/user/filmaffinity/output
    ```

- These are mapped into the container as `/input` and `/output` respectively.

### Example Directory Structure

```
your-project/
│
├── .env
├── docker-compose.yml
├── FilmAffinityListParser/
│   └── ...
├── data/
│   └── radarr/
│       └── config/
└── <your-exported-filmaffinity-file>   # Set via .env INPUT_PATH
```

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
