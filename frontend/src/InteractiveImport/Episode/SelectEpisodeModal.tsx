import React from 'react';
import Modal from 'Components/Modal/Modal';
import SelectEpisodeModalContent, {
  EpisodeSelectFileContext,
  SelectedEpisode,
} from './SelectEpisodeModalContent';

interface SelectEpisodeModalProps {
  isOpen: boolean;
  selectedIds: number[] | string[];
  episodeFiles?: EpisodeSelectFileContext[];
  seriesId?: number;
  seasonNumbers?: number[];
  selectedDetails?: string;
  isAnime: boolean;
  modalTitle: string;
  onEpisodesSelect(selectedEpisodes: SelectedEpisode[]): void;
  onModalClose(): void;
}

function SelectEpisodeModal(props: SelectEpisodeModalProps) {
  const {
    isOpen,
    selectedIds,
    episodeFiles,
    seriesId,
    seasonNumbers,
    selectedDetails,
    isAnime,
    modalTitle,
    onEpisodesSelect,
    onModalClose,
  } = props;

  return (
    <Modal isOpen={isOpen} onModalClose={onModalClose}>
      <SelectEpisodeModalContent
        selectedIds={selectedIds}
        episodeFiles={episodeFiles}
        seriesId={seriesId}
        seasonNumbers={seasonNumbers}
        selectedDetails={selectedDetails}
        isAnime={isAnime}
        modalTitle={modalTitle}
        onEpisodesSelect={onEpisodesSelect}
        onModalClose={onModalClose}
      />
    </Modal>
  );
}

export default SelectEpisodeModal;
