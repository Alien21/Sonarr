import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Label from 'Components/Label';
import VirtualTableRowCell from 'Components/Table/Cells/VirtualTableRowCell';
import styles from './SelectSeriesRow.css';

function stopPropagation(event) {
  event.stopPropagation();
}

class SelectSeriesRow extends Component {

  //
  // Listeners

  onPress = () => {
    this.props.onSeriesSelect(this.props.id);
  };

  //
  // Render

  render() {
    return (
      <>
        <VirtualTableRowCell className={styles.title}>
          {this.props.title}
        </VirtualTableRowCell>

        <VirtualTableRowCell className={styles.year}>
          {this.props.year}
        </VirtualTableRowCell>

        <VirtualTableRowCell className={styles.tvdbId}>
          <a
            className={styles.externalLink}
            href={`https://www.thetvdb.com/?tab=series&id=${this.props.tvdbId}`}
            rel="noreferrer"
            target="_blank"
            onClick={stopPropagation}
          >
            <Label>{this.props.tvdbId}</Label>
          </a>
        </VirtualTableRowCell>

        <VirtualTableRowCell className={styles.imdbId}>
          {
            this.props.imdbId ?
              <a
                className={styles.externalLink}
                href={`https://www.imdb.com/title/${this.props.imdbId}/`}
                rel="noreferrer"
                target="_blank"
                onClick={stopPropagation}
              >
                <Label>{this.props.imdbId}</Label>
              </a> :
              null
          }
        </VirtualTableRowCell>
      </>
    );
  }
}

SelectSeriesRow.propTypes = {
  id: PropTypes.number.isRequired,
  title: PropTypes.string.isRequired,
  tvdbId: PropTypes.number.isRequired,
  imdbId: PropTypes.string,
  year: PropTypes.number.isRequired,
  onSeriesSelect: PropTypes.func.isRequired
};

export default SelectSeriesRow;
