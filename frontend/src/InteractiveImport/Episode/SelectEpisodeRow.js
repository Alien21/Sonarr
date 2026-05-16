import PropTypes from 'prop-types';
import React, { Component } from 'react';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRowButton from 'Components/Table/TableRowButton';
import styles from './SelectEpisodeModalContent.css';

function getFileName(relativePath) {
  return relativePath.split(/[\\/]/).pop() ?? relativePath;
}

class SelectEpisodeRow extends Component {

  //
  // Listeners

  onPress = () => {
    const {
      id,
      isSelected
    } = this.props;

    this.props.onSelectedChange({ id, value: !isSelected });
  };

  //
  // Render

  render() {
    const {
      id,
      episodeNumber,
      absoluteEpisodeNumber,
      title,
      airDate,
      incomingFileNames,
      existingFileName,
      isAnime,
      isSelected,
      onSelectedChange
    } = this.props;

    return (
      <TableRowButton onPress={this.onPress}>
        <TableSelectCell
          id={id}
          isSelected={isSelected}
          onSelectedChange={onSelectedChange}
        />

        <TableRowCell>
          {episodeNumber}
          {isAnime ? ` (${absoluteEpisodeNumber})` : ''}
        </TableRowCell>

        <TableRowCell className={styles.episodeTitle}>
          {title}
        </TableRowCell>

        <TableRowCell className={styles.fileContext}>
          {
            incomingFileNames.length ?
              incomingFileNames.map((incomingFileName) => {
                return (
                  <div
                    key={incomingFileName}
                    className={styles.incomingFileName}
                    title={incomingFileName}
                  >
                    {getFileName(incomingFileName)}
                  </div>
                );
              }) :
              <div className={styles.noFileContext}>-</div>
          }

          {
            existingFileName ?
              <div
                className={styles.existingFileName}
                title={existingFileName}
              >
                {getFileName(existingFileName)}
              </div> :
              <div className={styles.noFileContext}>-</div>
          }
        </TableRowCell>

        <TableRowCell>
          {airDate}
        </TableRowCell>
      </TableRowButton>
    );
  }
}

SelectEpisodeRow.propTypes = {
  id: PropTypes.number.isRequired,
  episodeNumber: PropTypes.number.isRequired,
  absoluteEpisodeNumber: PropTypes.number.isRequired,
  title: PropTypes.string.isRequired,
  airDate: PropTypes.string.isRequired,
  incomingFileNames: PropTypes.arrayOf(PropTypes.string).isRequired,
  existingFileName: PropTypes.string,
  isAnime: PropTypes.bool.isRequired,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired
};

export default SelectEpisodeRow;
