import React, { useCallback } from 'react';
import Label from 'Components/Label';
import VirtualTableRowCell from 'Components/Table/Cells/VirtualTableRowCell';
import styles from './SelectSeriesRow.module.css';

interface SelectSeriesRowProps {
  title: string;
  tvdbId: number;
  imdbId?: string;
  year: number;
}

function SelectSeriesRow({
  title,
  year,
  tvdbId,
  imdbId,
}: SelectSeriesRowProps) {
  const handleExternalLinkClick = useCallback(
    (event: React.MouseEvent<HTMLAnchorElement>) => event.stopPropagation(),
    []
  );

  return (
    <>
      <VirtualTableRowCell className={styles.title}>
        {title}
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.year}>{year}</VirtualTableRowCell>

      <VirtualTableRowCell className={styles.tvdbId}>
        <a
          className={styles.externalLink}
          href={`https://www.thetvdb.com/?tab=series&id=${tvdbId}`}
          target="_blank"
          rel="noreferrer"
          onClick={handleExternalLinkClick}
        >
          <Label>{tvdbId}</Label>
        </a>
      </VirtualTableRowCell>

      <VirtualTableRowCell className={styles.imdbId}>
        {imdbId ? (
          <a
            className={styles.externalLink}
            href={`https://www.imdb.com/title/${imdbId}/`}
            target="_blank"
            rel="noreferrer"
            onClick={handleExternalLinkClick}
          >
            <Label>{imdbId}</Label>
          </a>
        ) : null}
      </VirtualTableRowCell>
    </>
  );
}

export default SelectSeriesRow;
